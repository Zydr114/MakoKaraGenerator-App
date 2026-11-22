using System.Collections.Immutable;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

public static partial class LyricContentEditUseCase
{
    /// <summary>在指定 tokenIndex 之前将一行拆分为两行，两侧无损保留 Token、Ruby 与重算后的时间包络。</summary>
    public static LyricContentEditResult SplitLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline, int tokenIndex) =>
        CommitPrepared(workspace, PrepareSplitLine(workspace, baseline, tokenIndex));

    /// <summary>纯准备断行候选。</summary>
    public static PreparedLyricContentEdit PrepareSplitLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline, int tokenIndex) =>
        Prepare(workspace, () => BuildSplitLine(workspace, baseline, tokenIndex), "lyrics.split-line", false, null);

    /// <summary>将当前行与上一行合并为一行，依次拼接文本、Token、Ruby 与外扩行时间包络。</summary>
    public static LyricContentEditResult MergeLines(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareMergeLines(workspace, baseline));

    /// <summary>纯准备合行候选。</summary>
    public static PreparedLyricContentEdit PrepareMergeLines(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        Prepare(workspace, () => BuildMergeLines(workspace, baseline), "lyrics.merge-lines", false, null);

    /// <summary>应用「全文快编」多行文本 Diff，在单条撤销记录内无损保留未改动字符的时间戳、注音与样式覆写。</summary>
    public static LyricContentEditResult ApplyTextDiff(ProjectWorkspace workspace, string branchId,
        IReadOnlyList<LyricTextDiffLineInput> lines, Action? onCreatingItem = null) =>
        CommitPrepared(workspace, PrepareApplyTextDiff(workspace, branchId, lines, onCreatingItem));

    /// <summary>纯准备「全文快编」分支 Diff 候选。</summary>
    public static PreparedLyricContentEdit PrepareApplyTextDiff(ProjectWorkspace workspace, string branchId,
        IReadOnlyList<LyricTextDiffLineInput> lines, Action? onCreatingItem = null) =>
        Prepare(workspace, () => BuildApplyTextDiff(workspace, branchId, lines, onCreatingItem), "lyrics.apply-text-diff", false, null);

    private static LyricContentEditResult BuildSplitLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline, int tokenIndex)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int lineIndex) is { } error)
            return Reject(workspace.Project, error);

        Project source = workspace.Project;
        LyricLine line = branch.Lines[lineIndex];
        if (tokenIndex < 1 || tokenIndex >= line.Tokens.Length)
            return Reject(source, LyricContentEditErrorCode.InvalidTarget, "断行位置必须位于行内第 2 个至最后一个元素之间。");

        try
        {
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            ImmutableArray<LyricToken> leftTokens = line.Tokens[..tokenIndex];
            ImmutableArray<LyricToken> rightTokens = line.Tokens[tokenIndex..];
            var leftIds = leftTokens.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var rightIds = rightTokens.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);

            (TimeRange? leftTime, TimingStatus leftStatus) = DeriveLineEnvelopeFromTokens(leftTokens);
            (TimeRange? rightTime, TimingStatus rightStatus) = DeriveLineEnvelopeFromTokens(rightTokens);

            LyricLine leftLine = line with
            {
                Text = string.Concat(leftTokens.Select(t => t.Text)),
                Time = leftTime,
                TimingStatus = leftStatus,
                Tokens = leftTokens,
                RubyAnnotations = [.. line.RubyAnnotations.Where(r => leftIds.Contains(r.BaseTokenId))],
            };
            LyricLine rightLine = new()
            {
                Id = allocator.NextLineId(),
                Text = string.Concat(rightTokens.Select(t => t.Text)),
                Time = rightTime,
                TimingStatus = rightStatus,
                Tokens = rightTokens,
                RubyAnnotations = [.. line.RubyAnnotations.Where(r => rightIds.Contains(r.BaseTokenId))],
            };

            if (!LyricTimingEditor.TryValidateLine(leftLine, out string reason)
                || !LyricTimingEditor.TryValidateLine(rightLine, out reason))
                return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);

            ImmutableArray<LyricLine> updatedLines = branch.Lines.SetItem(lineIndex, leftLine).Insert(lineIndex + 1, rightLine);
            Project candidate = ReplaceBranch(source, branch with { Lines = updatedLines });
            return PrepareCandidate(source, candidate, createdLineId: rightLine.Id, selectedLineId: rightLine.Id, insertedIndex: lineIndex + 1);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
    }

    private static LyricContentEditResult BuildMergeLines(ProjectWorkspace workspace, LyricContentEditBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int lineIndex) is { } error)
            return Reject(workspace.Project, error);

        Project source = workspace.Project;
        if (lineIndex < 1)
            return Reject(source, LyricContentEditErrorCode.InvalidTarget, "首行没有可合并的上一行。");

        LyricLine prev = branch.Lines[lineIndex - 1];
        LyricLine curr = branch.Lines[lineIndex];
        ImmutableArray<LyricToken> mergedTokens = prev.Tokens.AddRange(curr.Tokens);
        ImmutableArray<RubyAnnotation> mergedRubies = prev.RubyAnnotations.AddRange(curr.RubyAnnotations);

        long minStart = long.MaxValue;
        long maxEnd = long.MinValue;
        bool hasTime = false;
        void Include(TimeRange? range)
        {
            if (range is not { IsValid: true } valid) return;
            hasTime = true;
            if (valid.StartUs < minStart) minStart = valid.StartUs;
            if (valid.EndUs > maxEnd) maxEnd = valid.EndUs;
        }
        Include(prev.Time);
        Include(curr.Time);
        foreach (LyricToken token in mergedTokens) Include(token.Time);

        TimeRange? mergedTime = hasTime && maxEnd > minStart ? TimeRange.Create(minStart, maxEnd) : null;
        TimingStatus mergedStatus = mergedTime is not null ? TimingStatus.Edited : TimingStatus.Missing;

        LyricLine merged = prev with
        {
            Text = prev.Text + curr.Text,
            Time = mergedTime,
            TimingStatus = mergedStatus,
            Tokens = mergedTokens,
            RubyAnnotations = mergedRubies,
        };

        if (!LyricTimingEditor.TryValidateLine(merged, out string reason))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);

        ImmutableArray<LyricLine> updatedLines = branch.Lines.SetItem(lineIndex - 1, merged).RemoveAt(lineIndex);
        Project candidate = ReplaceBranch(source, branch with { Lines = updatedLines });
        return PrepareCandidate(source, candidate, selectedLineId: prev.Id);
    }

    private static LyricContentEditResult BuildApplyTextDiff(ProjectWorkspace workspace, string branchId,
        IReadOnlyList<LyricTextDiffLineInput> lines, Action? onCreatingItem)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(lines);
        Project source = workspace.Project;

        LyricBranch? branch = source.Lyrics.Branches.FirstOrDefault(b => b.Id == branchId);
        if (branch is null)
            return Reject(source, LyricContentEditErrorCode.TargetStale, $"找不到歌词来源 {branchId}。");

        var existingLineIds = branch.Lines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (LyricTextDiffLineInput item in lines)
        {
            if (item.SourceLineId is null) continue;
            if (!seenSourceIds.Add(item.SourceLineId))
                return Reject(source, LyricContentEditErrorCode.InvalidTarget, $"重复引用源行 {item.SourceLineId}。");
            if (!existingLineIds.Contains(item.SourceLineId))
                return Reject(source, LyricContentEditErrorCode.TargetStale, $"源行 {item.SourceLineId} 不存在。");
        }

        try
        {
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            string NextLineId() { onCreatingItem?.Invoke(); return allocator.NextLineId(); }
            string NextTokenId() { onCreatingItem?.Invoke(); return allocator.NextTokenId(); }

            LyricBranchTextDiffResult diff = LyricTextDiffAligner.ApplyToBranch(branch, lines, NextLineId, NextTokenId);
            foreach (LyricLine line in diff.Branch.Lines)
            {
                if (!LyricTimingEditor.TryValidateLine(line, out string reason))
                    return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
            }

            if (diff.Branch == branch)
                return Unchanged(source);

            Project candidate = ReplaceBranch(source, diff.Branch);
            string? selectedLineId = diff.Branch.Lines.IsEmpty ? null : diff.Branch.Lines[0].Id;
            return PrepareCandidate(source, candidate, preservedCount: diff.PreservedTokenCount,
                resetCount: diff.ResetTokenCount, selectedLineId: selectedLineId);
        }
        catch (LyricsIdentifierExhaustedException exception)
        {
            return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message);
        }
    }

    private static (TimeRange? Time, TimingStatus Status) DeriveLineEnvelopeFromTokens(ImmutableArray<LyricToken> tokens)
    {
        long minStart = long.MaxValue;
        long maxEnd = long.MinValue;
        bool hasAny = false;
        foreach (LyricToken token in tokens)
        {
            if (token.Time is not { IsValid: true } t) continue;
            hasAny = true;
            if (t.StartUs < minStart) minStart = t.StartUs;
            if (t.EndUs > maxEnd) maxEnd = t.EndUs;
        }
        return hasAny && maxEnd > minStart
            ? (TimeRange.Create(minStart, maxEnd), TimingStatus.Edited)
            : (null, TimingStatus.Missing);
    }

    /// <summary>只准备稳定行及已定时元素的整行平移；不修改工程、历史或重做。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="baseline">明确来源、稳定行与完整原行；不依赖当前界面选择。</param>
    /// <param name="deltaUs">精确有符号微秒；领域规则检查加法溢出和非负边界。</param>
    /// <param name="contentDurationUs">已准备媒体的微秒上界；非正表示未知。</param>
    /// <returns>可供展示的完整候选工程与时间摘要；拒绝保留当前工程引用。</returns>
    public static LyricTimingEditResult PreviewShiftLine(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs = 0) =>
        PrepareLineShift(workspace, baseline, deltaUs, contentDurationUs);

    /// <summary>原子平移整行及其已定时元素，沿用领域状态规则与一次不可合并历史。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="baseline">完整原行基线；其他来源或工程设置变化可保留。</param>
    /// <param name="deltaUs">精确有符号微秒。</param>
    /// <param name="contentDurationUs">已准备媒体上界；非正表示未知。</param>
    /// <returns>真实提交结果；零平移保留工程引用、dirty与redo。</returns>
    public static LyricTimingEditResult ShiftLine(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs = 0)
    {
        LyricTimingEditResult prepared = PrepareLineShift(workspace, baseline, deltaUs, contentDurationUs);
        if (!prepared.Accepted || !prepared.Changed) return prepared;
        workspace.History.BreakMergeGroup();
        Project committed = workspace.Execute(ProjectCommand.Create("line.shift", _ => prepared.Project));
        workspace.History.BreakMergeGroup();
        return new(true, committed, prepared.ChangeSet);
    }

    /// <summary>只准备固定原行内的元素重排，时间冲突要求显式选择时间槽重分配。</summary>
    /// <param name="workspace">单一工程所有者。</param>
    /// <param name="request">完整原行与稳定元素身份，目标下标属于该固定原行。</param>
    /// <returns>候选工程和结构摘要；预览不修改历史、dirty或redo。</returns>
    public static LyricContentEditResult PreviewReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request) =>
        PrepareTokenReorder(workspace, request);

    /// <summary>在明确来源中提交元素重排，保留元素身份、样式、注音和未定时状态。</summary>
    /// <param name="workspace">单一工程与历史所有者。</param>
    /// <param name="request">完整原行基线和被移动元素的稳定标识。</param>
    /// <returns>真实的一次历史提交；无变化保留原引用及重做。</returns>
    public static LyricContentEditResult ReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request)
    {
        return CommitPrepared(workspace, PrepareReorderToken(workspace, request));
    }

    /// <summary>固定元素重排候选；沿既有纯预演算法，不创建第二份历史。</summary>
    public static PreparedLyricContentEdit PrepareReorderToken(ProjectWorkspace workspace, LyricTokenReorderRequest request) =>
        Prepare(workspace, () =>
        {
            Project source = workspace.Project;
            LyricContentEditResult result = PrepareTokenReorder(workspace, request);
            return result.Accepted && result.Changed ? PrepareCandidate(source, result.Project) : result;
        }, "line.reorder", false, null);

    private static LyricTimingEditResult PrepareLineShift(ProjectWorkspace workspace,
        LyricContentEditBaseline baseline, long deltaUs, long contentDurationUs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        Project source = workspace.Project;
        if (CheckBaseline(workspace, baseline, out LyricBranch branch, out int index) is { } error)
            return RejectLineShift(source, error == LyricContentEditErrorCode.WorkspaceStale
                ? LyricTimingEditErrorCode.WorkspaceStale : LyricTimingEditErrorCode.TargetStale);
        bool accepted = false;
        string reason = string.Empty;
        Project candidate = LyricBranchEditor.Edit(source, branch.Id, view =>
        {
            accepted = LyricTimingEditor.TryShiftLineAndTokens(view, baseline.Target.LineId, deltaUs,
                out Project changed, out reason);
            return accepted ? changed : view;
        });
        if (!accepted) return RejectLineShift(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        LyricLine shifted = candidate.Lyrics.Branches.First(value => value.Id == branch.Id).Lines[index];
        if (contentDurationUs > 0 && shifted.Time is { } time && time.EndUs > contentDurationUs)
            return RejectLineShift(source, LyricTimingEditErrorCode.BeyondContentDuration);
        if (!ReferenceEquals(candidate, source) && !TryValidatePresentation(candidate, out reason))
            return RejectLineShift(source, LyricTimingEditErrorCode.DomainValidationFailed, reason);
        return new(true, candidate, LyricTimingEditUseCase.DescribeChanges(source, candidate));
    }

    private static LyricContentEditResult PrepareTokenReorder(ProjectWorkspace workspace, LyricTokenReorderRequest request)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.TokenId);
        Project source = workspace.Project;
        if (CheckBaseline(workspace, request.Baseline, out LyricBranch branch, out int index) is { } error)
            return Reject(source, error);
        if (!branch.Lines[index].Tokens.Any(token => token.Id == request.TokenId))
            return Reject(source, LyricContentEditErrorCode.TargetStale);
        bool accepted = false;
        string reason = string.Empty;
        Project candidate = LyricBranchEditor.Edit(source, branch.Id, view =>
        {
            accepted = LyricTokenReorderEditor.TryReorder(view, request.Baseline.Target.LineId,
                request.TokenId, request.DestinationIndex, request.ReassignTimeSlots, out Project changed, out reason);
            return accepted ? changed : view;
        });
        if (!accepted || (!ReferenceEquals(candidate, source) && !TryValidatePresentation(candidate, out reason)))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
        return new(true, candidate, DescribeChanges(source, candidate));
    }

    private static LyricTimingEditResult RejectLineShift(Project source, LyricTimingEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectTimingChangeSet.None, new(code, detail));
}
