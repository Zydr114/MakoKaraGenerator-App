using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>行与元素时间的统一编辑规则；拒绝时不修改工程。</summary>
public static partial class LyricTimingEditor
{
    /// <summary>检查一行中所有已定时元素的包含性和文本顺序；未定时元素不占时间槽。</summary>
    public static bool TryValidateLine(LyricLine line, out string reason)
    {
        ArgumentNullException.ThrowIfNull(line);
        long previousEndUs = -1;
        foreach (LyricToken token in line.Tokens)
        {
            if (token.Time is not { } time) continue;
            if (!time.IsValid)
            {
                reason = $"元素 {token.Id} 的时间区间无效。";
                return false;
            }
            if (line.Time is not { IsValid: true } lineTime)
            {
                reason = $"行 {line.Id} 尚无有效时间，不能为元素 {token.Id} 定时。";
                return false;
            }
            if (time.StartUs < lineTime.StartUs || time.EndUs > lineTime.EndUs)
            {
                reason = $"元素 {token.Id} 的时间越出行 {line.Id}。";
                return false;
            }
            if (time.StartUs < previousEndUs)
            {
                reason = $"元素 {token.Id} 与前面的元素时间重叠或倒序。";
                return false;
            }
            previousEndUs = time.EndUs;
        }
        reason = string.Empty;
        return true;
    }

    /// <summary>显式平移整行及其已定时元素；预览与提交共用这条纯规则。</summary>
    public static bool TryShiftLineAndTokens(
        Project project, string lineId, long deltaUs,
        out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lineId);
        result = project;
        int lineIndex = FindLine(project, lineId);
        if (lineIndex < 0)
        {
            reason = $"找不到歌词行 {lineId}。";
            return false;
        }
        LyricLine line = project.Lyrics.Lines[lineIndex];
        if (line.Time is not { IsValid: true } original)
        {
            reason = "请先设置有效行时间。";
            return false;
        }
        if (!TryValidateLine(line, out reason)) return false;
        if (deltaUs == 0) return true;

        try
        {
            long start = checked(original.StartUs + deltaUs);
            long end = checked(original.EndUs + deltaUs);
            if (!TimeRange.TryCreate(start, end, out TimeRange shifted))
            {
                reason = "平移后行时间不能为负。";
                return false;
            }

            var tokens = line.Tokens.ToBuilder();
            for (int index = 0; index < tokens.Count; index++)
            {
                LyricToken token = tokens[index];
                if (token.Time is not { IsValid: true } time) continue;
                tokens[index] = token with
                {
                    Time = TimeRange.Create(checked(time.StartUs + deltaUs),
                        checked(time.EndUs + deltaUs)),
                    TimingStatus = TimingStatus.Edited,
                };
            }

            LyricLine updated = line with
            {
                Time = shifted,
                TimingStatus = TimingStatus.Edited,
                Tokens = tokens.ToImmutable(),
                RubyAnnotations = [.. line.RubyAnnotations.Select(ruby => ruby with
                {
                    Segments = [.. ruby.Segments.Select(segment => segment.Time is { IsValid: true } time
                        ? segment with { Time = TimeRange.Create(checked(time.StartUs + deltaUs), checked(time.EndUs + deltaUs)), TimingStatus = TimingStatus.Edited }
                        : segment)],
                })],
            };
            if (!TryValidateLine(updated, out reason)) return false;
            result = project with
            {
                Lyrics = project.Lyrics with
                {
                    Lines = project.Lyrics.Lines.SetItem(lineIndex, updated),
                },
            };
            return TryAcceptDisplay(project, ref result, out reason);
        }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        {
            reason = "平移后的时间超出可表示范围或变为负数。";
            return false;
        }
    }

    /// <summary>编辑行区间；所有已定时元素必须仍在新区间内。</summary>
    public static bool TrySetLineTime(
        Project project, string lineId, long startUs, long endUs,
        out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lineId);
        result = project;

        int lineIndex = FindLine(project, lineId);
        if (lineIndex < 0)
        {
            reason = $"找不到歌词行 {lineId}。";
            return false;
        }

        if (!TimeRange.TryCreate(startUs, endUs, out TimeRange range))
        {
            reason = "行时间必须满足起点非负且终点晚于起点。";
            return false;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];
        foreach (LyricToken token in line.Tokens)
        {
            if (token.Time is { IsValid: true } tokenTime
                && (tokenTime.StartUs < startUs || tokenTime.EndUs > endUs))
            {
                reason = $"元素 {token.Id} 的时间越出新行区间。";
                return false;
            }
        }

        reason = string.Empty;
        if (line.Time == range)
        {
            return true;
        }

        LyricLine updated = line with { Time = range, TimingStatus = TimingStatus.Edited };
        if (!TryValidateLine(updated, out reason)) return false;
        result = project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = project.Lyrics.Lines.SetItem(lineIndex, updated),
            },
        };
        return TryAcceptDisplay(project, ref result, out reason);
    }

    private static bool TryAcceptDisplay(Project source, ref Project candidate, out string reason)
    {
        var report = Validation.PresentationValidator.Validate(candidate);
        reason = report.HasErrors ? report.Issues.First(issue => issue.Severity == Diagnostics.IssueSeverity.Error).Message : string.Empty;
        if (!report.HasErrors) return true;
        candidate = source;
        return false;
    }

    /// <summary>编辑单个元素区间；不得越出行区间或与其他元素重叠、倒序。</summary>
    public static bool TrySetTokenTime(
        Project project, string lineId, string tokenId, long startUs, long endUs,
        out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lineId);
        ArgumentNullException.ThrowIfNull(tokenId);
        result = project;

        int lineIndex = FindLine(project, lineId);
        if (lineIndex < 0)
        {
            reason = $"找不到歌词行 {lineId}。";
            return false;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];
        int tokenIndex = -1;
        for (int index = 0; index < line.Tokens.Length; index++)
        {
            if (line.Tokens[index].Id == tokenId)
            {
                tokenIndex = index;
                break;
            }
        }

        if (tokenIndex < 0)
        {
            reason = $"找不到元素 {tokenId}。";
            return false;
        }

        if (!TimeRange.TryCreate(startUs, endUs, out TimeRange range))
        {
            reason = "元素时间必须满足起点非负且终点晚于起点。";
            return false;
        }

        if (line.Time is not { IsValid: true } lineTime)
        {
            reason = $"歌词行 {lineId} 尚无有效行时间。";
            return false;
        }

        if (startUs < lineTime.StartUs || endUs > lineTime.EndUs)
        {
            reason = $"元素 {tokenId} 的时间必须处于行区间内。";
            return false;
        }

        for (int index = 0; index < line.Tokens.Length; index++)
        {
            if (index == tokenIndex || line.Tokens[index].Time is not { IsValid: true } other)
            {
                continue;
            }

            if ((index < tokenIndex && other.EndUs > startUs)
                || (index > tokenIndex && other.StartUs < endUs))
            {
                reason = $"元素 {tokenId} 与 {line.Tokens[index].Id} 的时间重叠或倒序。";
                return false;
            }
        }

        reason = string.Empty;
        LyricToken token = line.Tokens[tokenIndex];
        if (token.Time == range)
        {
            return true;
        }

        LyricLine updated = line with
        {
            Tokens = line.Tokens.SetItem(tokenIndex,
                token with { Time = range, TimingStatus = TimingStatus.Edited }),
        };
        if (!TryValidateLine(updated, out reason)) return false;
        result = project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = project.Lyrics.Lines.SetItem(lineIndex, updated),
            },
        };
        return true;
    }

    /// <summary>显式清除单个元素的时间；行区间和其他元素保持原值。</summary>
    public static bool TryClearTokenTime(
        Project project, string lineId, string tokenId,
        out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lineId);
        ArgumentNullException.ThrowIfNull(tokenId);
        result = project;

        int lineIndex = FindLine(project, lineId);
        if (lineIndex < 0)
        {
            reason = $"找不到歌词行 {lineId}。";
            return false;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];
        int tokenIndex = -1;
        for (int index = 0; index < line.Tokens.Length; index++)
        {
            if (line.Tokens[index].Id == tokenId)
            {
                tokenIndex = index;
                break;
            }
        }

        if (tokenIndex < 0)
        {
            reason = $"找不到元素 {tokenId}。";
            return false;
        }

        reason = string.Empty;
        LyricToken token = line.Tokens[tokenIndex];
        if (token.Time is null && token.TimingStatus == TimingStatus.Missing)
            return true;

        LyricLine updated = line with
        {
            Tokens = line.Tokens.SetItem(tokenIndex,
                token with { Time = null, TimingStatus = TimingStatus.Missing }),
        };
        result = project with
        {
            Lyrics = project.Lyrics with
            {
                Lines = project.Lyrics.Lines.SetItem(lineIndex, updated),
            },
        };
        return true;
    }

    private static int FindLine(Project project, string lineId)
    {
        for (int index = 0; index < project.Lyrics.Lines.Length; index++)
        {
            if (project.Lyrics.Lines[index].Id == lineId)
            {
                return index;
            }
        }

        return -1;
    }
}
