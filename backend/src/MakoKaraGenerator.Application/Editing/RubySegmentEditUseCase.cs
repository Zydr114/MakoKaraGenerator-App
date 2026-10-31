using System.Collections.Immutable;
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>独立注音时间、正文及序列的纯准备和一次历史提交；不复用 token 时间目标。</summary>
public static class RubySegmentEditUseCase
{
    /// <summary>按完整父链读取当前注音；基础元素不存在时拒绝悬空关联。</summary>
    public static RubyAnnotation? Read(Project project, RubyEditTarget target)
    {
        LyricLine? line = project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == target.BranchId)?
            .Lines.FirstOrDefault(line => line.Id == target.LineId);
        return line?.Tokens.Any(token => token.Id == target.TokenId) == true
            ? line.RubyAnnotations.FirstOrDefault(ruby => ruby.Id == target.RubyId && ruby.BaseTokenId == target.TokenId) : null;
    }

    /// <summary>准备设置单个片段的绝对端点；相同端点不改时间来源状态。</summary>
    public static PreparedRubySegmentEdit PrepareSetTime(ProjectWorkspace workspace, RubySegmentEditBaseline baseline,
        string segmentId, long startUs, long endUs) => PrepareBatch(workspace, [new(baseline, segmentId, startUs, endUs)], "ruby.segment.time");

    /// <summary>准备完整时间批次；重复目标或任何无效项拒绝全部候选。</summary>
    public static PreparedRubySegmentEdit PrepareBatch(ProjectWorkspace workspace, ImmutableArray<RubySegmentTimeEdit> edits,
        string name = "ruby.segment.times")
    {
        Project source = workspace.Project;
        if (edits.IsDefaultOrEmpty) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.InvalidBatch), name);
        var seen = new HashSet<(RubyEditTarget Target, string Segment)>();
        Project candidate = source;
        int changed = 0;
        foreach (RubySegmentTimeEdit edit in edits)
        {
            if (Check(workspace, edit.Baseline) is { } error) return Prepared(workspace, Reject(source, error), name);
            if (!seen.Add((edit.Baseline.Target, edit.SegmentId))) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DuplicateTarget), name);
            if (edit.StartUs < 0) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.InvalidStart), name);
            if (!TimeRange.TryCreate(edit.StartUs, edit.EndUs, out var time)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.InvalidRange), name);
            RubyAnnotation ruby = Read(candidate, edit.Baseline.Target)!;
            int index = Index(ruby, edit.SegmentId);
            if (index < 0) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.TargetStale), name);
            if (ruby.Segments[index].Time == time) continue;
            var segments = ruby.Segments.SetItem(index, ruby.Segments[index] with { Time = time, TimingStatus = TimingStatus.Edited });
            if (!RubySegmentEditor.TryReplaceSegments(ruby, segments, out var next)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DomainValidationFailed), name);
            candidate = Replace(candidate, edit.Baseline.Target, next); changed++;
        }
        return Prepared(workspace, AcceptCandidate(source, candidate, changed), name);
    }

    /// <summary>准备清除一个片段时间和状态；已缺失为无变化。</summary>
    public static PreparedRubySegmentEdit PrepareClearTime(ProjectWorkspace workspace, RubySegmentEditBaseline baseline, string segmentId) =>
        PrepareSegment(workspace, baseline, segmentId, segment => segment with { Time = null, TimingStatus = TimingStatus.Missing }, "ruby.segment.clear");

    /// <summary>准备修改一个片段正文，保留其身份、时间、状态和其它片段。</summary>
    public static PreparedRubySegmentEdit PrepareSetText(ProjectWorkspace workspace, RubySegmentEditBaseline baseline, string segmentId, string text) =>
        string.IsNullOrEmpty(text) ? Prepared(workspace, Reject(workspace.Project, RubySegmentEditErrorCode.EmptyText), "ruby.segment.text")
        : PrepareSegment(workspace, baseline, segmentId, segment => segment with { Text = text }, "ruby.segment.text");

    /// <summary>准备在明确片段后新增一个无时间片段；空前驱表示追加，新身份只分配一次。</summary>
    public static PreparedRubySegmentEdit PrepareAdd(ProjectWorkspace workspace, RubySegmentEditBaseline baseline,
        string text, string? afterSegmentId, Action<string>? beforeIdentifier = null)
    {
        const string name = "ruby.segment.add";
        Project source = workspace.Project;
        if (Check(workspace, baseline) is { } error) return Prepared(workspace, Reject(source, error), name);
        if (string.IsNullOrEmpty(text)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.EmptyText), name);
        int index = afterSegmentId is null ? baseline.Ruby.Segments.Length : Index(baseline.Ruby, afterSegmentId) + 1;
        if (index == 0) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.TargetStale), name);
        try
        {
            string id = new LyricsEditIdAllocator(source.Lyrics, beforeIdentifier).NextRubySegmentId();
            var segments = baseline.Ruby.Segments.Insert(index, RubyTextSegment.Create(id, text));
            if (!RubySegmentEditor.TryReplaceSegments(baseline.Ruby, segments, out var next)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DomainValidationFailed), name);
            return Prepared(workspace, AcceptCandidate(source, Replace(source, baseline.Target, next), 1) with { SelectedSegmentId = id, CreatedSegmentId = id }, name);
        }
        catch (LyricsIdentifierExhaustedException) { return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.IdentifierExhausted), name); }
    }

    /// <summary>准备移除片段；最后一个片段移除整个注音组，选择变为空。</summary>
    public static PreparedRubySegmentEdit PrepareRemove(ProjectWorkspace workspace, RubySegmentEditBaseline baseline, string segmentId)
    {
        const string name = "ruby.segment.remove";
        Project source = workspace.Project;
        if (Check(workspace, baseline) is { } error) return Prepared(workspace, Reject(source, error), name);
        int index = Index(baseline.Ruby, segmentId);
        if (index < 0) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.TargetStale), name);
        var segments = baseline.Ruby.Segments.RemoveAt(index);
        RubyAnnotation? next = null;
        if (!segments.IsEmpty && !RubySegmentEditor.TryReplaceSegments(baseline.Ruby, segments, out next)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DomainValidationFailed), name);
        return Prepared(workspace, AcceptCandidate(source, Replace(source, baseline.Target, next), 1) with
        { SelectedSegmentId = segments.IsEmpty ? null : segments[Math.Min(index, segments.Length - 1)].Id }, name);
    }

    /// <summary>准备明确的完整文本顺序；片段身份、正文、时间与状态整体移动。</summary>
    public static PreparedRubySegmentEdit PrepareReorder(ProjectWorkspace workspace, RubySegmentEditBaseline baseline, ImmutableArray<string> ids)
    {
        const string name = "ruby.segment.reorder";
        Project source = workspace.Project;
        if (Check(workspace, baseline) is { } error) return Prepared(workspace, Reject(source, error), name);
        if (ids.Length != baseline.Ruby.Segments.Length || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length
            || ids.Any(id => Index(baseline.Ruby, id) < 0)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.InvalidOrder), name);
        var map = baseline.Ruby.Segments.ToDictionary(segment => segment.Id, StringComparer.Ordinal);
        if (!RubySegmentEditor.TryReplaceSegments(baseline.Ruby, [.. ids.Select(id => map[id])], out var next)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DomainValidationFailed), name);
        return Prepared(workspace, AcceptCandidate(source, Replace(source, baseline.Target, next), next == baseline.Ruby ? 0 : ids.Length), name);
    }

    /// <summary>提交仍绑定原工作区、代次和原工程引用的候选；不跨请求合并历史。</summary>
    public static RubySegmentEditResult Commit(ProjectWorkspace workspace, PreparedRubySegmentEdit prepared)
    {
        if (!ReferenceEquals(workspace, prepared.Workspace) || workspace.Generation != prepared.Generation) return Reject(workspace.Project, RubySegmentEditErrorCode.WorkspaceStale);
        if (!ReferenceEquals(workspace.Project, prepared.Source)) return Reject(workspace.Project, RubySegmentEditErrorCode.TargetStale);
        if (!prepared.Result.Accepted || !prepared.Result.Changed) return prepared.Result;
        workspace.History.BreakMergeGroup();
        try { return prepared.Result with { Project = workspace.Execute(ProjectCommand.Create(prepared.Name, _ => prepared.Result.Project)) }; }
        finally { workspace.History.BreakMergeGroup(); }
    }

    private static PreparedRubySegmentEdit PrepareSegment(ProjectWorkspace workspace, RubySegmentEditBaseline baseline,
        string id, Func<RubyTextSegment, RubyTextSegment> edit, string name)
    {
        Project source = workspace.Project;
        if (Check(workspace, baseline) is { } error) return Prepared(workspace, Reject(source, error), name);
        int index = Index(baseline.Ruby, id);
        if (index < 0) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.TargetStale), name);
        if (!RubySegmentEditor.TryReplaceSegments(baseline.Ruby, baseline.Ruby.Segments.SetItem(index, edit(baseline.Ruby.Segments[index])), out var next)) return Prepared(workspace, Reject(source, RubySegmentEditErrorCode.DomainValidationFailed), name);
        return Prepared(workspace, AcceptCandidate(source, Replace(source, baseline.Target, next), next == baseline.Ruby ? 0 : 1), name);
    }
    private static RubySegmentEditErrorCode? Check(ProjectWorkspace workspace, RubySegmentEditBaseline baseline) =>
        !ReferenceEquals(workspace, baseline.Workspace) || workspace.Generation != baseline.Generation ? RubySegmentEditErrorCode.WorkspaceStale
        : Read(workspace.Project, baseline.Target) != baseline.Ruby ? RubySegmentEditErrorCode.TargetStale : null;
    private static int Index(RubyAnnotation ruby, string id)
    { for (int i = 0; i < ruby.Segments.Length; i++) if (ruby.Segments[i].Id == id) return i; return -1; }
    private static Project Replace(Project source, RubyEditTarget target, RubyAnnotation? ruby)
    {
        LyricBranch branch = source.Lyrics.Branches.First(value => value.Id == target.BranchId);
        int lineIndex = Array.FindIndex(branch.Lines.ToArray(), line => line.Id == target.LineId);
        LyricLine line = branch.Lines[lineIndex];
        int index = Array.FindIndex(line.RubyAnnotations.ToArray(), value => value.Id == target.RubyId);
        var rubies = ruby is null ? line.RubyAnnotations.RemoveAt(index) : line.RubyAnnotations.SetItem(index, ruby);
        LyricLine next = line with { RubyAnnotations = rubies };
        return next == line ? source : source with { Lyrics = source.Lyrics.ReplaceBranch(branch with { Lines = branch.Lines.SetItem(lineIndex, next) }) };
    }
    private static RubySegmentEditResult AcceptCandidate(Project source, Project candidate, int changed)
    {
        if (!ProjectEditSubmission.TryPrepare(source, candidate, out candidate, out string reason)) return Reject(source, RubySegmentEditErrorCode.DomainValidationFailed, reason);
        return new(true, candidate, !ReferenceEquals(source, candidate), ChangedSegmentCount: changed);
    }
    private static RubySegmentEditResult Reject(Project source, RubySegmentEditErrorCode error, string? detail = null) => new(false, source, false, error, detail);
    private static PreparedRubySegmentEdit Prepared(ProjectWorkspace workspace, RubySegmentEditResult result, string name) => new(workspace, workspace.Project, result, name);
}
