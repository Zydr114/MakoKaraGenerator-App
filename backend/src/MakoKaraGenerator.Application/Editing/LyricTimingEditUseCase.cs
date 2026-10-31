using System.Collections.Immutable;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>供桌面及独立宿主共用的行／元素时间组编辑用例，不持有第二份工程或历史状态。</summary>
public static partial class LyricTimingEditUseCase
{
    /// <summary>按明确分支与稳定 ID 读取目标时间；未定时目标也能成功读取。</summary>
    /// <param name="project">当前不可变工程。</param>
    /// <param name="target">要读取的稳定目标。</param>
    /// <param name="time">目标的原时间；未定时时为空。</param>
    /// <returns>目标仍存在时为真；不会按旧下标回退到其他目标。</returns>
    public static bool TryReadTime(Project project, LyricTimingEditTarget target, out TimeRange? time)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(target);
        time = null;
        LyricLine? line = project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == target.BranchId)?
            .Lines.FirstOrDefault(line => line.Id == target.LineId);
        if (line is null) return false;
        if (target.TokenId is null)
        {
            time = line.Time;
            return true;
        }
        LyricToken? token = line.Tokens.FirstOrDefault(token => token.Id == target.TokenId);
        if (token is null) return false;
        time = token.Time;
        return true;
    }

    /// <summary>验证并提交一组端点；成功变化执行一次不可合并命令，拒绝不改变历史。</summary>
    /// <param name="workspace">由调用方单一所有者调度的工作区。</param>
    /// <param name="request">稳定目标、输入基线、整数微秒与媒体边界。</param>
    /// <returns>接受状态、当前工程、最小时间变更摘要和中立拒绝原因。</returns>
    /// <remarks>
    /// 客户端输入会话与输入法门禁仍由适配层负责。本方法在工作区所有者线程同步执行，
    /// 不并发修改工作区，不跨请求保持历史合并组。无关的工程编辑不使原时间未变的输入过期。
    /// </remarks>
    public static LyricTimingEditResult Commit(ProjectWorkspace workspace, LyricTimingEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ExpectedWorkspace);
        ArgumentNullException.ThrowIfNull(request.Target);
        Project source = workspace.Project;
        if (!ReferenceEquals(workspace, request.ExpectedWorkspace)
            || workspace.Generation != request.ExpectedWorkspaceGeneration)
            return Reject(source, LyricTimingEditErrorCode.WorkspaceStale);
        if (!TryReadTime(source, request.Target, out TimeRange? current) || current != request.ExpectedTime)
            return Reject(source, LyricTimingEditErrorCode.TargetStale);

        long? start = request.StartUs ?? current?.StartUs;
        long? end = request.EndUs ?? current?.EndUs;
        if (start is not { } startUs || end is not { } endUs)
            return Reject(source, LyricTimingEditErrorCode.MissingEndpoint);
        if (request.ContentDurationUs > 0 && endUs > request.ContentDurationUs)
            return Reject(source, LyricTimingEditErrorCode.BeyondContentDuration);

        if (!TrySetTime(source, request.Target, startUs, endUs, out Project candidate, out string reason)
            || !TryValidate(candidate, out reason))
            return Reject(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);

        workspace.History.BreakMergeGroup();
        Project result = workspace.Execute(ProjectCommand.Create(
            request.Target.TokenId is null ? "line.time" : "token.time", _ => candidate));
        workspace.History.BreakMergeGroup();
        ProjectTimingChangeSet changes = ReferenceEquals(source, result)
            ? ProjectTimingChangeSet.None : new([request.Target]);
        return new LyricTimingEditResult(accepted: true, result, changes);
    }

    /// <summary>描述编辑或历史恢复中的纯时间变化；无法证明范围时要求完整刷新。</summary>
    /// <param name="before">编辑或历史恢复前的工程。</param>
    /// <param name="after">编辑或历史恢复后的工程。</param>
    /// <returns>稳定的行／元素目标；结构与任何时间以外的字段变化保守返回完整刷新。</returns>
    /// <remarks>
    /// 只检查发生引用变化的子树，并依赖当前完整记录相等合同；新增模型字段须同步更新相等合同与分类回归。
    /// </remarks>
    public static ProjectTimingChangeSet DescribeChanges(Project before, Project after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (ReferenceEquals(before, after)) return ProjectTimingChangeSet.None;
        if (after with { Lyrics = before.Lyrics } != before
            || after.Lyrics with { Branches = before.Lyrics.Branches } != before.Lyrics
            || before.Lyrics.Branches.Length != after.Lyrics.Branches.Length)
            return ProjectTimingChangeSet.FullRefresh;

        var targets = ImmutableArray.CreateBuilder<LyricTimingEditTarget>();
        for (int branchIndex = 0; branchIndex < before.Lyrics.Branches.Length; branchIndex++)
        {
            LyricBranch oldBranch = before.Lyrics.Branches[branchIndex];
            LyricBranch newBranch = after.Lyrics.Branches[branchIndex];
            if (ReferenceEquals(oldBranch, newBranch)) continue;
            if (newBranch with { Lines = oldBranch.Lines } != oldBranch
                || oldBranch.Lines.Length != newBranch.Lines.Length)
                return ProjectTimingChangeSet.FullRefresh;

            for (int lineIndex = 0; lineIndex < oldBranch.Lines.Length; lineIndex++)
            {
                LyricLine oldLine = oldBranch.Lines[lineIndex];
                LyricLine newLine = newBranch.Lines[lineIndex];
                if (ReferenceEquals(oldLine, newLine)) continue;
                if (newLine with { Time = oldLine.Time, TimingStatus = oldLine.TimingStatus, Tokens = oldLine.Tokens }
                        != oldLine || oldLine.Tokens.Length != newLine.Tokens.Length)
                    return ProjectTimingChangeSet.FullRefresh;

                if (newLine.Time != oldLine.Time || newLine.TimingStatus != oldLine.TimingStatus)
                    targets.Add(new LyricTimingEditTarget(oldBranch.Id, oldLine.Id));
                for (int tokenIndex = 0; tokenIndex < oldLine.Tokens.Length; tokenIndex++)
                {
                    LyricToken oldToken = oldLine.Tokens[tokenIndex];
                    LyricToken newToken = newLine.Tokens[tokenIndex];
                    if (ReferenceEquals(oldToken, newToken)) continue;
                    if (newToken with { Time = oldToken.Time, TimingStatus = oldToken.TimingStatus } != oldToken)
                        return ProjectTimingChangeSet.FullRefresh;
                    if (newToken.Time != oldToken.Time || newToken.TimingStatus != oldToken.TimingStatus)
                        targets.Add(new LyricTimingEditTarget(oldBranch.Id, oldLine.Id, oldToken.Id));
                }
            }
        }
        return targets.Count == 0 ? ProjectTimingChangeSet.None : new(targets.ToImmutable());
    }

    private static LyricTimingEditResult Reject(
        Project source, LyricTimingEditErrorCode code, string? detail = null) =>
        new(accepted: false, source, ProjectTimingChangeSet.None, new LyricTimingEditError(code, detail));
}
