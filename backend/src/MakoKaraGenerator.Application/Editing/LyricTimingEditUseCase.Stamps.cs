using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

public static partial class LyricTimingEditUseCase
{
    /// <summary>
    /// 将播放头记为稳定元素的起点。原元素没有可用终点时补一帧估算区间，
    /// 由一次 <c>token.stamp</c> 历史提交同时保存时间和 <see cref="TimingStatus.Estimated"/>。
    /// </summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="baseline">捕获时的工作区、代次、来源／行／元素及原时间。</param>
    /// <param name="playheadUs">播放头的整数微秒位置。</param>
    /// <param name="contentDurationUs">已准备媒体时长；非正值表示未知。</param>
    /// <returns>共享用例结果；客户端不需要依赖时间轴块下标。</returns>
    public static LyricTimingStampResult StampTokenStart(ProjectWorkspace workspace,
        LyricTimingEditBaseline baseline, long playheadUs, long contentDurationUs = 0)
    {
        LyricTimingEditResult? failure = ValidateBaseline(workspace, baseline, out TimeRange? current);
        if (failure is not null) return RejectStamp(failure);
        if (baseline.Target.TokenId is null)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.TokenRequired);

        long startUs = ClampPlayhead(playheadUs, contentDurationUs);
        bool estimatedPlaceholder = current is not { IsValid: true } time || time.EndUs <= startUs;
        long endUs;
        if (!estimatedPlaceholder)
        {
            endUs = current!.Value.EndUs;
        }
        else
        {
            try { endUs = checked(startUs + TimeUs.FromFrames(1, workspace.Project.Composition.FrameRate)); }
            catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
            { return RejectStamp(workspace.Project, LyricTimingEditErrorCode.NudgeOutOfBounds); }
        }

        if (contentDurationUs > 0 && endUs > contentDurationUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.BeyondContentDuration);

        if (!TrySetTime(workspace.Project, baseline.Target, startUs, endUs,
                out Project candidate, out string reason)
            || !TryValidate(candidate, out reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        if (estimatedPlaceholder)
        {
            candidate = SetTokenStatus(candidate, baseline.Target, TimingStatus.Estimated);
            if (!TryValidate(candidate, out reason))
                return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        }

        return SubmitStamp(workspace, candidate, estimatedPlaceholder);
    }

    /// <summary>将播放头记为稳定元素的终点，并把时间来源状态写为用户修改。</summary>
    /// <param name="workspace">唯一工程与历史所有者。</param>
    /// <param name="baseline">捕获时的工作区、代次、来源／行／元素及原时间。</param>
    /// <param name="playheadUs">播放头的整数微秒位置。</param>
    /// <param name="contentDurationUs">已准备媒体时长；非正值表示未知。</param>
    /// <returns>共享用例结果；成功后由客户端决定是否选择下一个元素。</returns>
    public static LyricTimingStampResult StampTokenEnd(ProjectWorkspace workspace,
        LyricTimingEditBaseline baseline, long playheadUs, long contentDurationUs = 0)
    {
        LyricTimingEditResult? failure = ValidateBaseline(workspace, baseline, out TimeRange? current);
        if (failure is not null) return RejectStamp(failure);
        if (baseline.Target.TokenId is null)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.TokenRequired);
        if (current is not { IsValid: true } time)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.Untimed);

        long endUs = ClampPlayhead(playheadUs, contentDurationUs);
        if (endUs <= time.StartUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.InvalidRange);
        if (contentDurationUs > 0 && endUs > contentDurationUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.BeyondContentDuration);

        if (!TrySetTime(workspace.Project, baseline.Target, time.StartUs, endUs,
                out Project candidate, out string reason)
            || !TryValidate(candidate, out reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        // 与普通端点设置区分：按 ] 明确确认终点，即使播放头恰好落在已有终点，
        // 也要把此前的估算／导入状态提升为用户修改。
        candidate = SetTokenStatus(candidate, baseline.Target, TimingStatus.Edited);
        if (!TryValidate(candidate, out reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        return SubmitStamp(workspace, candidate, estimatedPlaceholder: false);
    }

    /// <summary>
    /// 原子连打（J/K）：自动初始化／外扩无时间行与多拍注音父级词元包络，含 1.5s 忘按 L 兜底截断保护。
    /// 每次按键产生一条独立的 <c>beat.strike</c> 可撤销历史。
    /// </summary>
    public static LyricTimingStampResult StrikeBeat(
        ProjectWorkspace workspace,
        LyricBeatTarget? closeTarget,
        LyricBeatTarget openTarget,
        long playheadUs,
        long contentDurationUs = 0,
        long closeGuardGapUs = LyricTimingEditor.DefaultCloseGuardGapUs,
        long fallbackDurationUs = LyricTimingEditor.DefaultCloseFallbackDurationUs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(openTarget);

        long clampedPlayhead = ClampPlayhead(playheadUs, contentDurationUs);
        long oneFrameUs;
        try { oneFrameUs = TimeUs.FromFrames(1, workspace.Project.Composition.FrameRate); }
        catch (Exception exception) when (exception is OverflowException or ArgumentOutOfRangeException)
        { return RejectStamp(workspace.Project, LyricTimingEditErrorCode.NudgeOutOfBounds); }

        if (!BeatExists(workspace.Project, openTarget)
            || (closeTarget is not null && !BeatExists(workspace.Project, closeTarget)))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.TargetStale);

        if (closeTarget is not null && TryReadBeatTime(workspace.Project, closeTarget, out TimeRange? prevClose)
            && prevClose is { IsValid: true } prev && clampedPlayhead <= prev.StartUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.InvalidRange, "收尾时刻必须晚于上一拍的起点。");

        if (!LyricTimingEditor.TryStrikeBeat(
                workspace.Project, closeTarget, openTarget, clampedPlayhead, oneFrameUs,
                closeGuardGapUs, fallbackDurationUs, out Project candidate, out BeatStrikeOutcome outcome, out string reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        if (contentDurationUs > 0)
        {
            if (TryReadBeatTime(candidate, openTarget, out TimeRange? openTime) && openTime is { IsValid: true } ot && ot.EndUs > contentDurationUs)
                return RejectStamp(workspace.Project, LyricTimingEditErrorCode.BeyondContentDuration);
            if (closeTarget is not null && TryReadBeatTime(candidate, closeTarget, out TimeRange? closeTime) && closeTime is { IsValid: true } ct && ct.EndUs > contentDurationUs)
                return RejectStamp(workspace.Project, LyricTimingEditErrorCode.BeyondContentDuration);
        }

        if (!TryValidate(candidate, out reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        return SubmitBeatStamp(workspace, candidate, "beat.strike", outcome.EstimatedPlaceholder, outcome.CloseGuardApplied);
    }

    /// <summary>
    /// 原子显式收尾（L）：定格目标拍点终点并同步外扩父级词元与行尾包络，产生独立 <c>beat.close</c> 历史。
    /// </summary>
    public static LyricTimingStampResult CloseBeat(
        ProjectWorkspace workspace,
        LyricBeatTarget target,
        long playheadUs,
        long contentDurationUs = 0)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(target);

        if (!BeatExists(workspace.Project, target))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.TargetStale);

        if (!TryReadBeatTime(workspace.Project, target, out TimeRange? current) || current is not { IsValid: true } currentTime)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.Untimed);

        long clampedEnd = ClampPlayhead(playheadUs, contentDurationUs);
        if (clampedEnd <= currentTime.StartUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.InvalidRange);
        if (contentDurationUs > 0 && clampedEnd > contentDurationUs)
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.BeyondContentDuration);

        if (!LyricTimingEditor.TryCloseBeat(workspace.Project, target, clampedEnd, out Project candidate, out string reason)
            || !TryValidate(candidate, out reason))
            return RejectStamp(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        return SubmitBeatStamp(workspace, candidate, "beat.close", estimatedPlaceholder: false, closeGuardApplied: null);
    }

    /// <summary>翻转指定实义词元的句中收尾提示覆写值（auto → !auto → auto）。</summary>
    public static LyricTimingEditResult TogglePhraseEndHint(
        ProjectWorkspace workspace,
        LyricTimingEditTarget target)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(target);
        if (target.TokenId is null)
            return Reject(workspace.Project, LyricTimingEditErrorCode.TokenRequired);

        LyricBranch? branch = workspace.Project.Lyrics.Branches.FirstOrDefault(b => b.Id == target.BranchId);
        LyricLine? line = branch?.Lines.FirstOrDefault(l => l.Id == target.LineId);
        LyricToken? token = line?.Tokens.FirstOrDefault(t => t.Id == target.TokenId);
        if (token is null)
            return Reject(workspace.Project, LyricTimingEditErrorCode.TargetStale);

        if (!LyricTimingEditor.TryTogglePhraseEndHint(
                workspace.Project, target.BranchId, target.LineId, target.TokenId,
                out Project candidate, out _, out string reason))
            return Reject(workspace.Project, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        Project source = workspace.Project;
        if (ReferenceEquals(source, candidate))
        {
            workspace.History.BreakMergeGroup();
            return new(true, source, ProjectTimingChangeSet.None);
        }

        workspace.History.BreakMergeGroup();
        Project committed = workspace.Execute(ProjectCommand.Create("token.phrase-end-hint", _ => candidate));
        workspace.History.BreakMergeGroup();
        return new(true, committed, DescribeChanges(source, committed));
    }

    private static bool BeatExists(Project project, LyricBeatTarget target) =>
        TryReadBeatTime(project, target, out _);

    private static bool TryReadBeatTime(Project project, LyricBeatTarget target, out TimeRange? time)
    {
        time = null;
        if (!target.HasValidShape) return false;
        LyricLine? line = project.Lyrics.Branches.FirstOrDefault(b => b.Id == target.BranchId)?
            .Lines.FirstOrDefault(l => l.Id == target.LineId);
        LyricToken? token = line?.Tokens.FirstOrDefault(t => t.Id == target.TokenId);
        if (line is null || token is null) return false;
        if (!target.IsRubySegment)
        {
            time = token.Time;
            return true;
        }
        RubyAnnotation? ruby = line.RubyAnnotations.FirstOrDefault(r => r.Id == target.RubyId && r.BaseTokenId == target.TokenId);
        RubyTextSegment? segment = ruby?.Segments.FirstOrDefault(s => s.Id == target.SegmentId);
        if (segment is null) return false;
        time = segment.Time;
        return true;
    }

    private static LyricTimingStampResult SubmitBeatStamp(ProjectWorkspace workspace, Project candidate,
        string commandName, bool estimatedPlaceholder, bool? closeGuardApplied)
    {
        Project source = workspace.Project;
        if (ReferenceEquals(source, candidate))
        {
            workspace.History.BreakMergeGroup();
            return new(true, source, ProjectTimingChangeSet.None, estimatedPlaceholder: false, closeGuardApplied: closeGuardApplied);
        }

        workspace.History.BreakMergeGroup();
        Project result = workspace.Execute(ProjectCommand.Create(commandName, _ => candidate));
        workspace.History.BreakMergeGroup();
        return new(true, result, DescribeChanges(source, result),
            estimatedPlaceholder: estimatedPlaceholder, closeGuardApplied: closeGuardApplied);
    }

    private static long ClampPlayhead(long playheadUs, long contentDurationUs)
    {
        long nonNegative = Math.Max(0L, playheadUs);
        return contentDurationUs > 0 ? Math.Min(nonNegative, contentDurationUs) : nonNegative;
    }

    private static Project SetTokenStatus(Project source, LyricTimingEditTarget target, TimingStatus status)
    {
        return LyricBranchEditor.Edit(source, target.BranchId, view =>
        {
            LyricLine? line = view.Lyrics.Branches.FirstOrDefault(branch => branch.Id == target.BranchId)?
                .Lines.FirstOrDefault(candidate => candidate.Id == target.LineId);
            if (line is null || target.TokenId is not { } tokenId)
                return view;
            int tokenIndex = -1;
            for (int index = 0; index < line.Tokens.Length; index++)
            {
                if (line.Tokens[index].Id == tokenId)
                {
                    tokenIndex = index;
                    break;
                }
            }
            if (tokenIndex < 0 || line.Tokens[tokenIndex].Time is not { IsValid: true })
                return view;
            LyricToken token = line.Tokens[tokenIndex];
            if (token.TimingStatus == status)
                return view;
            LyricLine updated = line with
            {
                Tokens = line.Tokens.SetItem(tokenIndex, token with { TimingStatus = status }),
            };
            LyricBranch branch = view.Lyrics.Branches.First(candidate => candidate.Id == target.BranchId);
            int lineIndex = branch.Lines.IndexOf(line);
            if (lineIndex < 0) return view;
            LyricBranch updatedBranch = branch with { Lines = branch.Lines.SetItem(lineIndex, updated) };
            return view with { Lyrics = view.Lyrics.ReplaceBranch(updatedBranch) };
        });
    }

    private static LyricTimingStampResult SubmitStamp(ProjectWorkspace workspace, Project candidate,
        bool estimatedPlaceholder)
    {
        Project source = workspace.Project;
        if (ReferenceEquals(source, candidate))
        {
            workspace.History.BreakMergeGroup();
            return new(true, source, ProjectTimingChangeSet.None, estimatedPlaceholder: false);
        }

        workspace.History.BreakMergeGroup();
        Project result = workspace.Execute(ProjectCommand.Create("token.stamp", _ => candidate));
        workspace.History.BreakMergeGroup();
        return new(true, result, DescribeChanges(source, result), estimatedPlaceholder: estimatedPlaceholder);
    }

    private static LyricTimingStampResult RejectStamp(LyricTimingEditResult failure) =>
        new(false, failure.Project, failure.ChangeSet, failure.Error);

    private static LyricTimingStampResult RejectStamp(Project project, LyricTimingEditErrorCode code,
        string? detail = null) => new(false, project, ProjectTimingChangeSet.None, new(code, detail));
}
