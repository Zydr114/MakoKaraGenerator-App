using System.Collections.Immutable;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Text;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>共享元素拆分、合并与来源重切；由工作区所有者同步调度。</summary>
/// <remarks>复用原领域的时间、样式与注音语义，不自动重绑悬空注音；客户端持有模态草稿。</remarks>
public static class LyricTokenEditUseCase
{
    /// <summary>按原 Unicode 与时间规则拆分；保持一次独立历史。</summary>
    public static LyricTokenEditResult SplitToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareSplitToken(workspace, baseline));

    /// <summary>纯准备稳定元素拆分，不提交工作区或历史。</summary>
    public static PreparedLyricTokenEdit PrepareSplitToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline,
        LyricTokenPreparationObservers? observers = null) => Prepare(workspace,
            () => BuildSplitToken(workspace, baseline, observers), "line.split");

    /// <summary>合并相邻后元素；保持第一身份和原缺失时间语义。</summary>
    public static LyricTokenEditResult MergeToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline) =>
        CommitPrepared(workspace, PrepareMergeToken(workspace, baseline));

    /// <summary>纯准备稳定元素合并，不提交任何历史。</summary>
    public static PreparedLyricTokenEdit PrepareMergeToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline,
        LyricTokenPreparationObservers? observers = null) => Prepare(workspace,
            () => BuildMergeToken(workspace, baseline, observers), "line.merge");

    /// <summary>按精确手工边界重切整行。</summary>
    public static LyricTokenEditResult ResplitLine(ProjectWorkspace workspace, LyricLineTokenSplitRequest request) =>
        CommitPrepared(workspace, PrepareResplitLine(workspace, request));

    /// <summary>纯准备整行手工重切，实际段和标识分配可由宿主观察。</summary>
    public static PreparedLyricTokenEdit PrepareResplitLine(ProjectWorkspace workspace, LyricLineTokenSplitRequest request,
        LyricTokenPreparationObservers? observers = null) => Prepare(workspace,
            () => BuildResplitLine(workspace, request, observers), "line.split-rule");

    /// <summary>按既有规则原子重切明确来源，全部行一次历史。</summary>
    public static LyricTokenEditResult ResplitBranch(ProjectWorkspace workspace, LyricBranchTokenSplitRequest request) =>
        CommitPrepared(workspace, PrepareResplitBranch(workspace, request));

    /// <summary>纯准备整个来源；每个改变行完成时可预检，任何失败不采用前面的行。</summary>
    public static PreparedLyricTokenEdit PrepareResplitBranch(ProjectWorkspace workspace, LyricBranchTokenSplitRequest request,
        LyricTokenPreparationObservers? observers = null) => Prepare(workspace,
            () => BuildResplitBranch(workspace, request, observers), "lyrics.split-rule");

    /// <summary>仅提交原工作区、代次及工程引用仍有效的固定候选，不重复生成标识。</summary>
    public static LyricTokenEditResult CommitPrepared(ProjectWorkspace workspace, PreparedLyricTokenEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(workspace, prepared.Workspace) || workspace.Generation != prepared.Generation)
            return Reject(workspace.Project, LyricContentEditErrorCode.WorkspaceStale);
        if (!ReferenceEquals(workspace.Project, prepared.Source))
            return Reject(workspace.Project, LyricContentEditErrorCode.TargetStale);
        LyricTokenEditResult result = prepared.Result;
        if (!result.Accepted || !result.Changed) return result;
        workspace.History.BreakMergeGroup();
        Project committed = workspace.Execute(ProjectCommand.Create(prepared.CommandName, _ => result.Project));
        workspace.History.BreakMergeGroup();
        return new(true, committed, result.ChangeSet, selectedLineId: result.SelectedLineId,
            selectedTokenId: result.SelectedTokenId, changedLineCount: result.ChangedLineCount,
            preservedCount: result.PreservedCount, createdCount: result.CreatedCount, estimatedCount: result.EstimatedCount);
    }

    private static PreparedLyricTokenEdit Prepare(ProjectWorkspace workspace, Func<LyricTokenEditResult> build, string name)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        Project source = workspace.Project;
        long generation = workspace.Generation;
        return new(workspace, generation, source, build(), name);
    }

    private static string NewTokenId(LyricsEditIdAllocator allocator, LyricTokenPreparationObservers? observers)
    {
        observers?.OnCreatingToken?.Invoke();
        return allocator.NextTokenId();
    }

    /// <summary>在稳定的已定时元素中间按 Unicode 文本元素数拆分，左右时间标为估算。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">明确来源、行、元素与完整原行。</param>
    /// <returns>一次历史变化及建议选择的新右元素；不可拆时合法无变化。</returns>
    /// <param name="observers">宿主可选的实际分配与完成行观察器。</param>
    private static LyricTokenEditResult BuildSplitToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline, LyricTokenPreparationObservers? observers)
    {
        if (CheckLine(workspace, baseline, tokenRequired: true, out LyricBranch branch, out int lineIndex) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        LyricLine line = branch.Lines[lineIndex];
        observers?.OnStartingLine?.Invoke(lineIndex);
        int tokenIndex = FindToken(line, baseline.Target.TokenId!);
        LyricToken token = line.Tokens[tokenIndex];
        if (token.Time is not { IsValid: true } time) return Unchanged(source);
        int elementCount = GraphemeSplitter.Enumerate(token.Text).Count();
        if (elementCount < 2) return Unchanged(source);
        int cut = elementCount / 2;
        long splitUs = checked(time.StartUs + RoundRatio(time.DurationUs, cut, elementCount));
        if (splitUs <= time.StartUs || splitUs >= time.EndUs) return Unchanged(source);
        try
        {
            int firstLength = GraphemeSplitter.Enumerate(token.Text).Skip(cut).First().Start;
            LyricToken left = token with { Text = token.Text[..firstLength],
                Time = TimeRange.Create(time.StartUs, splitUs), TimingStatus = TimingStatus.Estimated };
            LyricToken right = LyricToken.Create(NewTokenId(new LyricsEditIdAllocator(source.Lyrics), observers),
                token.Text[firstLength..], TimeRange.Create(splitUs, time.EndUs), TimingStatus.Estimated);
            LyricLine changed = line with { Tokens = line.Tokens.SetItem(tokenIndex, left).Insert(tokenIndex + 1, right) };
            return PrepareLine(source, branch, lineIndex, changed, observers, "line.split", right.Id,
                preservedCount: 1, createdCount: 1, estimatedCount: 2);
        }
        catch (LyricsIdentifierExhaustedException exception)
        { return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message); }
    }

    /// <summary>将稳定元素与当前同一行的相邻后元素合并；任一未定时则结果未定时。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="baseline">明确来源、行、元素与完整原行。</param>
    /// <returns>保留第一元素标识、属性与原注音记录，建议选择合并后的元素。</returns>
    /// <param name="observers">宿主可选的实际分配与完成行观察器。</param>
    private static LyricTokenEditResult BuildMergeToken(ProjectWorkspace workspace, LyricContentEditBaseline baseline, LyricTokenPreparationObservers? observers)
    {
        if (CheckLine(workspace, baseline, tokenRequired: true, out LyricBranch branch, out int lineIndex) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        LyricLine line = branch.Lines[lineIndex];
        observers?.OnStartingLine?.Invoke(lineIndex);
        int tokenIndex = FindToken(line, baseline.Target.TokenId!);
        if (tokenIndex + 1 >= line.Tokens.Length) return Unchanged(source);
        LyricToken first = line.Tokens[tokenIndex], second = line.Tokens[tokenIndex + 1];
        if (first.Time is { IsValid: false } || second.Time is { IsValid: false })
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, "待合并元素的时间区间无效。");
        TimeRange? range = null;
        TimingStatus status = TimingStatus.Missing;
        if (first.Time is { } firstTime && second.Time is { } secondTime)
        {
            long start = Math.Min(firstTime.StartUs, secondTime.StartUs);
            long end = Math.Max(firstTime.EndUs, secondTime.EndUs);
            if (end <= start) return Unchanged(source);
            range = TimeRange.Create(start, end);
            status = first.TimingStatus == second.TimingStatus ? first.TimingStatus : TimingStatus.Estimated;
        }
        LyricToken merged = first with { Text = first.Text + second.Text, Time = range, TimingStatus = status };
        LyricLine changed = line with { Tokens = line.Tokens.SetItem(tokenIndex, merged).RemoveAt(tokenIndex + 1) };
        return PrepareLine(source, branch, lineIndex, changed, observers, "line.merge", merged.Id);
    }

    /// <summary>将固定手工边界应用到稳定行，复用原 Core 重切和计数规则。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">完整原行与 Unicode 安全边界。</param>
    /// <returns>一次历史或合法无变化；原正文、行时间、注音和关联不变。</returns>
    /// <param name="observers">宿主可选的实际分配与完成行观察器。</param>
    private static LyricTokenEditResult BuildResplitLine(ProjectWorkspace workspace, LyricLineTokenSplitRequest request, LyricTokenPreparationObservers? observers)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (CheckLine(workspace, request.Baseline, tokenRequired: false, out LyricBranch branch, out int lineIndex) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        LyricLine line = branch.Lines[lineIndex];
        if (!ValidSegments(line.Text, request.Segments)) return Reject(source, LyricContentEditErrorCode.InvalidTarget);
        try
        {
            observers?.OnStartingLine?.Invoke(lineIndex);
            if (observers?.OnCreatingSegment is { } observeSegment)
                foreach (string _ in request.Segments) observeSegment();
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            var result = LyricLineTokenSplitter.Apply(line, request.Segments, request.EstimateTimes,
                () => NewTokenId(allocator, observers));
            if (ReferenceEquals(result.Line, line)) return Unchanged(source);
            return PrepareLine(source, branch, lineIndex, result.Line, observers, "line.split-rule", null,
                result.PreservedCount, result.CreatedCount, result.EstimatedCount);
        }
        catch (LyricsIdentifierExhaustedException exception)
        { return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message); }
        catch (ArgumentException exception)
        { return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, exception.Message); }
    }

    /// <summary>一次重切明确来源的全部行，使用全歌词命名空间 ID 分配与完整来源原值门禁。</summary>
    /// <param name="workspace">当前工作区。</param>
    /// <param name="request">捕获的明确来源和原规则。</param>
    /// <returns>实际改变行数与原领域计数；任何拒绝均不提交部分行。</returns>
    /// <param name="observers">宿主可选的实际分配与完成行观察器。</param>
    private static LyricTokenEditResult BuildResplitBranch(ProjectWorkspace workspace, LyricBranchTokenSplitRequest request, LyricTokenPreparationObservers? observers)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        var baseline = request.Baseline;
        ArgumentNullException.ThrowIfNull(baseline.ExpectedBranch);
        if (CheckWorkspace(workspace, baseline.ExpectedWorkspace, baseline.ExpectedWorkspaceGeneration) is { } error)
            return Reject(workspace.Project, error);
        Project source = workspace.Project;
        LyricBranch? branch = source.Lyrics.Branches.FirstOrDefault(branch => branch.Id == baseline.BranchId);
        if (branch is null || branch != baseline.ExpectedBranch) return Reject(source, LyricContentEditErrorCode.TargetStale);
        if (!Enum.IsDefined(request.Rule)) return Reject(source, LyricContentEditErrorCode.InvalidTarget);
        if (request.Rule == TokenSplitRule.None || branch.Lines.IsEmpty) return Unchanged(source);
        try
        {
            var allocator = new LyricsEditIdAllocator(source.Lyrics);
            var lines = branch.Lines.ToBuilder();
            int changed = 0, preserved = 0, created = 0, estimated = 0;
            for (int index = 0; index < lines.Count; index++)
            {
                observers?.OnStartingLine?.Invoke(index);
                var result = LyricLineTokenSplitter.Apply(lines[index], request.Rule, request.EstimateTimes,
                    () => NewTokenId(allocator, observers), observers?.OnCreatingSegment);
                if (ReferenceEquals(result.Line, lines[index])) continue;
                if (!LyricTimingEditor.TryValidateLine(result.Line, out string reason))
                    return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
                observers?.OnPreparedLine?.Invoke(index, result.Line);
                lines[index] = result.Line;
                changed++; preserved += result.PreservedCount; created += result.CreatedCount; estimated += result.EstimatedCount;
            }
            if (changed == 0) return Unchanged(source);
            return PrepareCandidate(source, branch with { Lines = lines.ToImmutable() }, "lyrics.split-rule",
                null, null, changed, preserved, created, estimated);
        }
        catch (LyricsIdentifierExhaustedException exception)
        { return Reject(source, LyricContentEditErrorCode.IdentifierExhausted, exception.Message); }
        catch (ArgumentException exception)
        { return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, exception.Message); }
    }

    private static LyricContentEditErrorCode? CheckWorkspace(ProjectWorkspace workspace, ProjectWorkspace expected, long generation)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return ReferenceEquals(workspace, expected) && workspace.Generation == generation
            ? null : LyricContentEditErrorCode.WorkspaceStale;
    }

    private static LyricContentEditErrorCode? CheckLine(ProjectWorkspace workspace, LyricContentEditBaseline baseline,
        bool tokenRequired, out LyricBranch branch, out int index)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(baseline.Target);
        ArgumentNullException.ThrowIfNull(baseline.ExpectedLine);
        branch = null!; index = -1;
        if (CheckWorkspace(workspace, baseline.ExpectedWorkspace, baseline.ExpectedWorkspaceGeneration) is { } error) return error;
        if (tokenRequired != (baseline.Target.TokenId is not null)) return LyricContentEditErrorCode.InvalidTarget;
        branch = workspace.Project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == baseline.Target.BranchId)!;
        if (branch is null) return LyricContentEditErrorCode.TargetStale;
        for (int i = 0; i < branch.Lines.Length; i++) if (branch.Lines[i].Id == baseline.Target.LineId) { index = i; break; }
        if (index < 0 || branch.Lines[index] != baseline.ExpectedLine
            || tokenRequired && FindToken(branch.Lines[index], baseline.Target.TokenId!) < 0)
            return LyricContentEditErrorCode.TargetStale;
        return null;
    }

    private static int FindToken(LyricLine line, string id)
    {
        for (int index = 0; index < line.Tokens.Length; index++) if (line.Tokens[index].Id == id) return index;
        return -1;
    }

    private static bool ValidSegments(string text, ImmutableArray<string> segments)
    {
        if (text.Length == 0) return segments.IsDefaultOrEmpty;
        if (segments.IsDefaultOrEmpty) return false;
        int cursor = 0;
        foreach (string segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment.Length > text.Length - cursor
                || !text.AsSpan(cursor, segment.Length).SequenceEqual(segment.AsSpan())) return false;
            cursor += segment.Length;
        }
        if (cursor != text.Length) return false;
        int segmentIndex = 0;
        int boundary = segments[0].Length;
        foreach (TextElementRange element in GraphemeSplitter.Enumerate(text))
        {
            if (element.End < boundary) continue;
            if (element.End > boundary) return false;
            if (++segmentIndex == segments.Length) return element.End == text.Length;
            boundary += segments[segmentIndex].Length;
        }
        return false;
    }

    private static LyricTokenEditResult PrepareLine(Project source, LyricBranch branch,
        int index, LyricLine line, LyricTokenPreparationObservers? observers, string name, string? selectedTokenId, int preservedCount = 0,
        int createdCount = 0, int estimatedCount = 0)
    {
        line = RubySegmentEditor.RemoveDetachedGroups(line);
        if (!LyricTimingEditor.TryValidateLine(line, out string reason))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
        observers?.OnPreparedLine?.Invoke(index, line);
        return PrepareCandidate(source, branch with { Lines = branch.Lines.SetItem(index, line) }, name,
            line.Id, selectedTokenId, 1, preservedCount, createdCount, estimatedCount);
    }

    private static LyricTokenEditResult PrepareCandidate(Project source, LyricBranch branch,
        string name, string? selectedLineId, string? selectedTokenId, int changedLines, int preserved, int created, int estimated)
    {
        Project candidate = source with { Lyrics = source.Lyrics.ReplaceBranch(branch) };
        if (!ProjectEditSubmission.TryPrepare(source, candidate, out Project result, out string reason))
            return Reject(source, LyricContentEditErrorCode.DomainValidationFailed, reason);
        if (ReferenceEquals(result, source)) return Unchanged(source);
        return new(true, result, LyricContentEditUseCase.DescribeChanges(source, result), selectedLineId: selectedLineId,
            selectedTokenId: selectedTokenId, changedLineCount: changedLines,
            preservedCount: preserved, createdCount: created, estimatedCount: estimated);
    }

    private static LyricTokenEditResult Unchanged(Project source) => new(true, source, ProjectLyricsChangeSet.None);
    private static LyricTokenEditResult Reject(Project source, LyricContentEditErrorCode code, string? detail = null) =>
        new(false, source, ProjectLyricsChangeSet.None, new(code, detail));

    private static long RoundRatio(long value, long numerator, long denominator)
    {
        if (numerator <= 0 || value <= 0) return 0;
        Int128 product = (Int128)value * numerator;
        Int128 quotient = product / denominator;
        Int128 remainder = product % denominator;
        Int128 twiceRemainder = remainder * 2;
        if (twiceRemainder > denominator || twiceRemainder == denominator && (quotient & 1) != 0)
            quotient++;
        return checked((long)quotient);
    }
}
