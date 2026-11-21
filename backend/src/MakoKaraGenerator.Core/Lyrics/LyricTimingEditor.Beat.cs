using System.Collections.Immutable;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>连打与显式收尾的稳定拍点目标：指向实义词元，或指向该词元下某个注音分段。</summary>
public sealed record LyricBeatTarget(
    string BranchId,
    string LineId,
    string TokenId,
    string? RubyId = null,
    string? SegmentId = null)
{
    /// <summary>是否指向注音分段而非基础词元。</summary>
    public bool IsRubySegment => RubyId is not null || SegmentId is not null;

    /// <summary>检查父链字段是否完整且无半填状态。</summary>
    public bool HasValidShape =>
        !string.IsNullOrWhiteSpace(BranchId)
        && !string.IsNullOrWhiteSpace(LineId)
        && !string.IsNullOrWhiteSpace(TokenId)
        && ((RubyId is null && SegmentId is null)
            || (!string.IsNullOrWhiteSpace(RubyId) && !string.IsNullOrWhiteSpace(SegmentId)));
}

/// <summary>一次原子连打的实际语义结果。</summary>
public sealed record BeatStrikeOutcome(bool EstimatedPlaceholder, bool CloseGuardApplied);

public static partial class LyricTimingEditor
{
    /// <summary>忘按 L 收尾时的默认跨间奏截断阈值（1.5 秒）。</summary>
    public const long DefaultCloseGuardGapUs = 1_500_000L;

    /// <summary>触发忘按 L 保护时的默认尾音兜底时长（600 毫秒）。</summary>
    public const long DefaultCloseFallbackDurationUs = 600_000L;

    /// <summary>
    /// 原子执行右手连打（J/K）：闭合上一未收尾拍（可选，含 1.5s 跨间奏兜底截断），
    /// 同时将当前待打拍的起点设为 <paramref name="playheadUs"/>，并自动初始化／外扩父级词元及行时间包络。
    /// </summary>
    public static bool TryStrikeBeat(
        Project project,
        LyricBeatTarget? closeTarget,
        LyricBeatTarget openTarget,
        long playheadUs,
        long oneFrameUs,
        long closeGuardGapUs,
        long fallbackDurationUs,
        out Project result,
        out BeatStrikeOutcome outcome,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(openTarget);
        result = project;
        outcome = new(false, false);

        if (playheadUs < 0)
        {
            reason = "播放头时间不能为负。";
            return false;
        }
        if (oneFrameUs <= 0)
        {
            reason = "单帧时长必须大于 0。";
            return false;
        }
        if (closeGuardGapUs < 0 || fallbackDurationUs <= 0)
        {
            reason = "截断保护阈值不能为负，兜底尾音时长必须大于 0。";
            return false;
        }
        if (!openTarget.HasValidShape)
        {
            reason = "待开唱拍点目标无效。";
            return false;
        }
        if (closeTarget is not null)
        {
            if (!closeTarget.HasValidShape)
            {
                reason = "待收尾拍点目标无效。";
                return false;
            }
            if (!string.Equals(closeTarget.BranchId, openTarget.BranchId, StringComparison.Ordinal))
            {
                reason = "收尾目标与开唱目标必须属于同一歌词来源。";
                return false;
            }
            if (closeTarget == openTarget)
            {
                reason = "收尾目标与开唱目标不能是同一个拍点。";
                return false;
            }
        }

        LyricBranch? branch = project.Lyrics.Branches.FirstOrDefault(b => b.Id == openTarget.BranchId);
        if (branch is null)
        {
            reason = $"找不到歌词来源 {openTarget.BranchId}。";
            return false;
        }

        var lines = branch.Lines.ToBuilder();
        var touchedLines = new HashSet<int>();
        var touchedRubyTokens = new HashSet<(int LineIndex, int TokenIndex)>();
        bool closeGuardApplied = false;

        if (!TryLocateBeat(lines, openTarget, out int openLineIdx, out int openTokenIdx, out int openRubyIdx, out int openSegIdx, out reason))
            return false;

        int closeLineIdx = -1, closeTokenIdx = -1, closeRubyIdx = -1, closeSegIdx = -1;
        if (closeTarget is not null)
        {
            if (!TryLocateBeat(lines, closeTarget, out closeLineIdx, out closeTokenIdx, out closeRubyIdx, out closeSegIdx, out reason))
                return false;

            TimeRange? prevTime = ReadBeatTime(lines[closeLineIdx], closeTokenIdx, closeRubyIdx, closeSegIdx);
            if (prevTime is { IsValid: true } prev)
            {
                if (playheadUs <= prev.StartUs)
                {
                    reason = "收尾时刻必须晚于上一拍的起点。";
                    return false;
                }

                LyricLine closeLine = lines[closeLineIdx];
                bool isLastBeatOfToken = closeRubyIdx < 0 || closeSegIdx == closeLine.RubyAnnotations[closeRubyIdx].Segments.Length - 1;
                bool isBoundary = closeLineIdx != openLineIdx
                    || (isLastBeatOfToken && (LyricTokenSemantics.IsLastContentToken(closeLine.Tokens, closeTokenIdx)
                        || LyricTokenSemantics.HasPhraseEndHint(closeLine.Tokens, closeTokenIdx)));

                long gapUs = playheadUs - prev.StartUs;
                long closeEndUs;
                TimingStatus closeStatus;
                if (isBoundary && closeGuardGapUs > 0 && gapUs > closeGuardGapUs)
                {
                    try { closeEndUs = Math.Min(checked(prev.StartUs + fallbackDurationUs), playheadUs); }
                    catch (OverflowException) { closeEndUs = playheadUs; }
                    closeStatus = TimingStatus.Estimated;
                    closeGuardApplied = true;
                }
                else
                {
                    closeEndUs = playheadUs;
                    closeStatus = TimingStatus.Edited;
                }

                lines[closeLineIdx] = WriteBeatTime(closeLine, closeTokenIdx, closeRubyIdx, closeSegIdx,
                    TimeRange.Create(prev.StartUs, closeEndUs), closeStatus);
                touchedLines.Add(closeLineIdx);
                if (closeRubyIdx >= 0) touchedRubyTokens.Add((closeLineIdx, closeTokenIdx));
            }
        }

        // Open current beat
        LyricLine openLine = lines[openLineIdx];
        TimeRange? currentOpenTime = ReadBeatTime(openLine, openTokenIdx, openRubyIdx, openSegIdx);
        long? nextTimedTokenStart = null;
        if (openRubyIdx < 0)
        {
            for (int i = openTokenIdx + 1; i < openLine.Tokens.Length; i++)
            {
                if (openLine.Tokens[i].Time is { IsValid: true } nextTime)
                {
                    nextTimedTokenStart = nextTime.StartUs;
                    break;
                }
            }
        }

        bool estimatedPlaceholder;
        long openEndUs;
        if (currentOpenTime is { IsValid: true } existingOpen
            && existingOpen.EndUs > playheadUs
            && (openRubyIdx >= 0 || nextTimedTokenStart is null || existingOpen.EndUs <= nextTimedTokenStart.Value))
        {
            openEndUs = existingOpen.EndUs;
            estimatedPlaceholder = false;
        }
        else
        {
            long candidateEnd;
            try { candidateEnd = checked(playheadUs + oneFrameUs); }
            catch (OverflowException)
            {
                reason = "开唱时间超出可表示范围。";
                return false;
            }
            if (nextTimedTokenStart is { } nextStart && nextStart > playheadUs && candidateEnd > nextStart)
                candidateEnd = nextStart;
            openEndUs = candidateEnd;
            estimatedPlaceholder = true;
        }

        TimingStatus openStatus = estimatedPlaceholder ? TimingStatus.Estimated : TimingStatus.Edited;
        lines[openLineIdx] = WriteBeatTime(openLine, openTokenIdx, openRubyIdx, openSegIdx,
            TimeRange.Create(playheadUs, openEndUs), openStatus);
        touchedLines.Add(openLineIdx);
        if (openRubyIdx >= 0) touchedRubyTokens.Add((openLineIdx, openTokenIdx));

        foreach ((int lineIndex, int tokenIndex) in touchedRubyTokens)
        {
            lines[lineIndex] = RecomputeRubyParentTokenEnvelope(lines[lineIndex], tokenIndex);
        }

        foreach (int lineIndex in touchedLines)
        {
            LyricLine expanded = ExpandLineTimeEnvelope(lines[lineIndex]);
            if (!TryValidateLine(expanded, out reason))
                return false;
            lines[lineIndex] = expanded;
        }

        LyricBranch updatedBranch = branch with { Lines = lines.ToImmutable() };
        Project candidate = project with { Lyrics = project.Lyrics.ReplaceBranch(updatedBranch) };
        if (!TryAcceptDisplay(project, ref candidate, out reason))
            return false;

        result = candidate;
        outcome = new(estimatedPlaceholder, closeGuardApplied);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// 原子执行显式收尾（L）：将目标拍点（实义词元或注音分段）的终点定格为 <paramref name="playheadUs"/>，
    /// 并同步更新父级基础词元与行尾时间包络。
    /// </summary>
    public static bool TryCloseBeat(
        Project project,
        LyricBeatTarget target,
        long playheadUs,
        out Project result,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(target);
        result = project;

        if (playheadUs < 0)
        {
            reason = "播放头时间不能为负。";
            return false;
        }
        if (!target.HasValidShape)
        {
            reason = "拍点目标无效。";
            return false;
        }

        LyricBranch? branch = project.Lyrics.Branches.FirstOrDefault(b => b.Id == target.BranchId);
        if (branch is null)
        {
            reason = $"找不到歌词来源 {target.BranchId}。";
            return false;
        }

        var lines = branch.Lines.ToBuilder();
        if (!TryLocateBeat(lines, target, out int lineIdx, out int tokenIdx, out int rubyIdx, out int segIdx, out reason))
            return false;

        LyricLine line = lines[lineIdx];
        TimeRange? currentTime = ReadBeatTime(line, tokenIdx, rubyIdx, segIdx);
        if (currentTime is not { IsValid: true } current)
        {
            reason = "该拍点尚未开唱，无法显式收尾。";
            return false;
        }
        if (playheadUs <= current.StartUs)
        {
            reason = "收尾时刻必须晚于当前拍点的起点。";
            return false;
        }

        LyricLine updated = WriteBeatTime(line, tokenIdx, rubyIdx, segIdx,
            TimeRange.Create(current.StartUs, playheadUs), TimingStatus.Edited);
        if (rubyIdx >= 0)
            updated = RecomputeRubyParentTokenEnvelope(updated, tokenIdx);
        updated = ExpandLineTimeEnvelope(updated);

        if (!TryValidateLine(updated, out reason))
            return false;

        lines[lineIdx] = updated;
        Project candidate = project with { Lyrics = project.Lyrics.ReplaceBranch(branch with { Lines = lines.ToImmutable() }) };
        if (!TryAcceptDisplay(project, ref candidate, out reason))
            return false;

        result = candidate;
        reason = string.Empty;
        return true;
    }

    /// <summary>翻转指定实义词元的句中收尾提示覆写值（auto → !auto → auto）。</summary>
    public static bool TryTogglePhraseEndHint(
        Project project,
        string branchId,
        string lineId,
        string tokenId,
        out Project result,
        out bool? newHint,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(project);
        result = project;
        newHint = null;

        LyricBranch? branch = project.Lyrics.Branches.FirstOrDefault(b => b.Id == branchId);
        if (branch is null) { reason = $"找不到歌词来源 {branchId}。"; return false; }

        int lineIndex = -1;
        for (int i = 0; i < branch.Lines.Length; i++)
        {
            if (branch.Lines[i].Id == lineId) { lineIndex = i; break; }
        }
        if (lineIndex < 0) { reason = $"找不到歌词行 {lineId}。"; return false; }

        LyricLine line = branch.Lines[lineIndex];
        int tokenIndex = -1;
        for (int i = 0; i < line.Tokens.Length; i++)
        {
            if (line.Tokens[i].Id == tokenId) { tokenIndex = i; break; }
        }
        if (tokenIndex < 0) { reason = $"找不到元素 {tokenId}。"; return false; }

        LyricToken token = line.Tokens[tokenIndex];
        if (LyricTokenSemantics.Classify(token.Text) != LyricTokenKind.Content)
        {
            reason = "仅实义词元支持切换句中停顿收尾提示。";
            return false;
        }

        bool autoHint = LyricTokenSemantics.DerivePhraseEndHint(line.Tokens, tokenIndex);
        bool currentEffective = token.PhraseEndHint ?? autoHint;
        bool targetEffective = !currentEffective;
        bool? nextOverride = targetEffective == autoHint ? null : targetEffective;

        LyricLine updated = line with
        {
            Tokens = line.Tokens.SetItem(tokenIndex, token with { PhraseEndHint = nextOverride }),
        };
        result = project with
        {
            Lyrics = project.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(lineIndex, updated) }),
        };
        newHint = nextOverride;
        reason = string.Empty;
        return true;
    }

    private static bool TryLocateBeat(
        ImmutableArray<LyricLine>.Builder lines,
        LyricBeatTarget target,
        out int lineIdx,
        out int tokenIdx,
        out int rubyIdx,
        out int segIdx,
        out string reason)
    {
        lineIdx = -1;
        tokenIdx = -1;
        rubyIdx = -1;
        segIdx = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Id == target.LineId) { lineIdx = i; break; }
        }
        if (lineIdx < 0)
        {
            reason = $"找不到歌词行 {target.LineId}。";
            return false;
        }

        LyricLine line = lines[lineIdx];
        for (int i = 0; i < line.Tokens.Length; i++)
        {
            if (line.Tokens[i].Id == target.TokenId) { tokenIdx = i; break; }
        }
        if (tokenIdx < 0)
        {
            reason = $"找不到元素 {target.TokenId}。";
            return false;
        }

        if (target.IsRubySegment)
        {
            for (int i = 0; i < line.RubyAnnotations.Length; i++)
            {
                RubyAnnotation ruby = line.RubyAnnotations[i];
                if (ruby.Id == target.RubyId && ruby.BaseTokenId == target.TokenId)
                {
                    rubyIdx = i;
                    break;
                }
            }
            if (rubyIdx < 0)
            {
                reason = $"找不到注音组 {target.RubyId}。";
                return false;
            }
            RubyAnnotation foundRuby = line.RubyAnnotations[rubyIdx];
            for (int i = 0; i < foundRuby.Segments.Length; i++)
            {
                if (foundRuby.Segments[i].Id == target.SegmentId) { segIdx = i; break; }
            }
            if (segIdx < 0)
            {
                reason = $"找不到注音分段 {target.SegmentId}。";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static TimeRange? ReadBeatTime(LyricLine line, int tokenIdx, int rubyIdx, int segIdx) =>
        rubyIdx >= 0 ? line.RubyAnnotations[rubyIdx].Segments[segIdx].Time : line.Tokens[tokenIdx].Time;

    private static LyricLine WriteBeatTime(LyricLine line, int tokenIdx, int rubyIdx, int segIdx, TimeRange time, TimingStatus status)
    {
        if (rubyIdx < 0)
        {
            LyricToken token = line.Tokens[tokenIdx];
            return line with
            {
                Tokens = line.Tokens.SetItem(tokenIdx, token with { Time = time, TimingStatus = status }),
            };
        }

        RubyAnnotation ruby = line.RubyAnnotations[rubyIdx];
        RubyTextSegment seg = ruby.Segments[segIdx];
        var nextSegments = ruby.Segments.SetItem(segIdx, seg with { Time = time, TimingStatus = status });
        RubySegmentEditor.TryReplaceSegments(ruby, nextSegments, out RubyAnnotation nextRuby);
        return line with
        {
            RubyAnnotations = line.RubyAnnotations.SetItem(rubyIdx, nextRuby),
        };
    }

    private static LyricLine RecomputeRubyParentTokenEnvelope(LyricLine line, int tokenIndex)
    {
        LyricToken token = line.Tokens[tokenIndex];
        long minStart = long.MaxValue;
        long maxEnd = long.MinValue;
        bool hasAny = false;
        bool anyEstimated = false;

        foreach (RubyAnnotation ruby in line.RubyAnnotations)
        {
            if (ruby.BaseTokenId != token.Id) continue;
            foreach (RubyTextSegment seg in ruby.Segments)
            {
                if (seg.Time is not { IsValid: true } segTime) continue;
                hasAny = true;
                if (segTime.StartUs < minStart) minStart = segTime.StartUs;
                if (segTime.EndUs > maxEnd) maxEnd = segTime.EndUs;
                if (seg.TimingStatus == TimingStatus.Estimated) anyEstimated = true;
            }
        }

        if (!hasAny || maxEnd <= minStart) return line;
        TimeRange envelope = TimeRange.Create(minStart, maxEnd);
        TimingStatus status = anyEstimated ? TimingStatus.Estimated : TimingStatus.Edited;
        if (token.Time == envelope && token.TimingStatus == status) return line;
        return line with
        {
            Tokens = line.Tokens.SetItem(tokenIndex, token with { Time = envelope, TimingStatus = status }),
        };
    }

    private static LyricLine ExpandLineTimeEnvelope(LyricLine line)
    {
        long minStart = long.MaxValue;
        long maxEnd = long.MinValue;
        bool hasAny = false;

        foreach (LyricToken token in line.Tokens)
        {
            if (token.Time is not { IsValid: true } t) continue;
            hasAny = true;
            if (t.StartUs < minStart) minStart = t.StartUs;
            if (t.EndUs > maxEnd) maxEnd = t.EndUs;
        }

        if (!hasAny || maxEnd <= minStart) return line;
        if (line.Time is { IsValid: true } current)
        {
            long newStart = Math.Min(current.StartUs, minStart);
            long newEnd = Math.Max(current.EndUs, maxEnd);
            if (newStart == current.StartUs && newEnd == current.EndUs) return line;
            return line with { Time = TimeRange.Create(newStart, newEnd), TimingStatus = TimingStatus.Edited };
        }

        return line with { Time = TimeRange.Create(minStart, maxEnd), TimingStatus = TimingStatus.Edited };
    }
}
