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
