using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>只在一行内重排元素，保留稳定 ID 与元素样式。</summary>
public static class LyricTokenReorderEditor
{
    /// <summary>预览或提交重排；需重新分配时间槽时由调用界面显式确认。</summary>
    public static bool TryReorder(
        Project project, string lineId, string tokenId, int destinationIndex,
        bool reassignTimeSlots, out Project result, out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(lineId);
        ArgumentNullException.ThrowIfNull(tokenId);
        result = project;
        int lineIndex = -1;
        for (int index = 0; index < project.Lyrics.Lines.Length; index++)
        {
            if (project.Lyrics.Lines[index].Id == lineId) { lineIndex = index; break; }
        }

        if (lineIndex < 0)
        {
            reason = $"找不到歌词行 {lineId}。";
            return false;
        }

        LyricLine line = project.Lyrics.Lines[lineIndex];
        if (line.Text != string.Concat(line.Tokens.Select(static token => token.Text)))
        {
            reason = $"行 {lineId} 的文本与元素文本不一致，不能安全重排。";
            return false;
        }
        int sourceIndex = -1;
        for (int index = 0; index < line.Tokens.Length; index++)
        {
            if (line.Tokens[index].Id == tokenId) { sourceIndex = index; break; }
        }

        if (sourceIndex < 0 || destinationIndex < 0 || destinationIndex >= line.Tokens.Length)
        {
            reason = "元素或目标位置不存在。";
            return false;
        }

        reason = string.Empty;
        if (sourceIndex == destinationIndex) return true;

        List<LyricToken> ordered = [.. line.Tokens];
        LyricToken moved = ordered[sourceIndex];
        ordered.RemoveAt(sourceIndex);
        ordered.Insert(destinationIndex, moved);

        if (reassignTimeSlots)
        {
            TimeRange[] slots = [.. line.Tokens
                .Where(static token => token.Time is { IsValid: true })
                .Select(static token => token.Time!.Value)];
            int slotIndex = 0;
            for (int index = 0; index < ordered.Count; index++)
            {
                if (ordered[index].Time is not { IsValid: true }) continue;
                TimeRange slot = slots[slotIndex++];
                if (ordered[index].Time != slot || ordered[index].TimingStatus != TimingStatus.Edited)
                {
                    ordered[index] = ordered[index] with
                    {
                        Time = slot,
                        TimingStatus = TimingStatus.Edited,
                    };
                }
            }
        }

        long previousEnd = -1;
        foreach (LyricToken token in ordered)
        {
            if (token.Time is not { IsValid: true } time) continue;
            if (line.Time is not { IsValid: true } lineTime
                || time.StartUs < lineTime.StartUs || time.EndUs > lineTime.EndUs
                || time.StartUs < previousEnd)
            {
                reason = $"重排后元素 {token.Id} 的时间越行、重叠或倒序。";
                return false;
            }
            previousEnd = time.EndUs;
        }

        LyricLine updated = line with
        {
            Tokens = [.. ordered],
            Text = string.Concat(ordered.Select(static token => token.Text)),
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
}
