using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>自动生成差异的状态。</summary>
public enum LeadInCandidateStatus
{
    /// <summary>可以新增。</summary>
    New,
    /// <summary>建议更新已有自动条目。</summary>
    Update,
    /// <summary>建议删除不再符合规则的自动条目，仍需确认。</summary>
    Remove,
    /// <summary>保留已有或受保护的结果。</summary>
    Preserved,
    /// <summary>无法生成，显示原因。</summary>
    Skipped,
}
/// <summary>候选的可本地化原因。</summary>
public enum LeadInCandidateReason
{
    /// <summary>可生成或已经与建议相同。</summary>
    None,
    /// <summary>目标没有有效行时间。</summary>
    MissingLineTime,
    /// <summary>前序有未知时间歌词，不能判断演唱空档。</summary>
    UnknownPrecedingTime,
    /// <summary>没有足够时间放入完整提示。</summary>
    TooShort,
    /// <summary>未达到空档阈值或所选范围。</summary>
    NotEligible,
    /// <summary>已有手工结果。</summary>
    ExistingManual,
    /// <summary>已明确禁用。</summary>
    Disabled,
    /// <summary>用户明确排除。</summary>
    Excluded,
    /// <summary>与其他演唱或提示区间冲突。</summary>
    TimingConflict,
}
/// <summary>一条只读候选，不直接写工程。</summary>
public sealed record LeadInCandidate
{
    /// <summary>目标源行。</summary>
    public required string TargetLineId { get; init; }
    /// <summary>差异状态。</summary>
    public LeadInCandidateStatus Status { get; init; }
    /// <summary>原因。</summary>
    public LeadInCandidateReason Reason { get; init; }
    /// <summary>已有条目。</summary>
    public LyricLeadIn? Existing { get; init; }
    /// <summary>建议新增或替换的完整条目。</summary>
    public LyricLeadIn? Proposed { get; init; }
    /// <summary>派生准备区间。</summary>
    public TimeRange? Range { get; init; }
    /// <summary>起唱时间依据，保持原状态。</summary>
    public LeadInAnchor? Anchor { get; init; }
}
/// <summary>纯分析结果及输入错误。</summary>
/// <param name="Candidates">按源顺序排列的差异。</param>
/// <param name="Report">参数和程序的校验报告。</param>
public sealed record LeadInAnalysis(ImmutableArray<LeadInCandidate> Candidates, ValidationReport Report);

/// <summary>按源演唱空档分析和明确应用候选，不使用音量猜测间奏。</summary>
public static class LeadInGeneration
{
    /// <summary>分析整个显示程序；源歌词、提示容器与工程均不改变。</summary>
    public static LeadInAnalysis Analyze(Project project, string programId, LeadInGenerationSettings settings)
    {
        var program = project.LyricPresentation.Programs.FirstOrDefault(item => item.Id == programId);
        var branch = project.Lyrics.Branches.FirstOrDefault(item => item.Id == program?.BranchId);
        var node = settings.IndicatorContainerId is null ? null : project.LyricPresentation.FindNode(settings.IndicatorContainerId);
        var policy = program?.AnimationPolicy ?? (branch?.Role == LyricBranchRole.Primary ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);
        if (program is null || branch is null || branch.TimingMode != LyricBranchTimingMode.Independent || policy != LyricBranchAnimationPolicy.Karaoke
            || settings.DurationUs <= 0 || settings.GapThresholdUs < 0 || node?.Content is not CountdownSequenceContent sequence
            || sequence.MarkNodeIds.IsEmpty || settings.DurationUs < sequence.MarkNodeIds.Length)
            return new([], Error("自动前奏需要独立逐字显示程序、提示容器和有效的时长／阈值。"));
        var configured = project with { LyricPresentation = project.LyricPresentation with { Programs = project.LyricPresentation.Programs.Replace(program, program with { LeadInGeneration = settings }) } };
        var inputReport = Validation.PresentationValidator.Validate(configured);
        if (inputReport.HasErrors) return new([], inputReport);
        List<LeadInCandidate> candidates = [];
        long previousEnd = 0;
        bool hasKnown = false, unknown = false;
        foreach (var line in branch.Lines)
        {
            var existing = program.LeadIns.FirstOrDefault(cue => cue.TargetLineId == line.Id);
            var anchor = LeadInTiming.Anchor(line);
            var occupancy = LeadInTiming.Occupancy(line);
            var reason = anchor is null ? LeadInCandidateReason.MissingLineTime
                : unknown ? LeadInCandidateReason.UnknownPrecedingTime
                : !hasKnown ? (settings.IncludeOpening ? LeadInCandidateReason.None : LeadInCandidateReason.NotEligible)
                : settings.IncludeLongGaps && anchor.Value.TimeUs >= previousEnd && anchor.Value.TimeUs - previousEnd >= settings.GapThresholdUs
                    ? LeadInCandidateReason.None : LeadInCandidateReason.NotEligible;
            var range = LeadInTiming.Range(line, settings.DurationUs);
            if (reason == LeadInCandidateReason.None && range is null) reason = LeadInCandidateReason.TooShort;
            if (reason == LeadInCandidateReason.None && range is { } interval
                && (branch.Lines.Any(other => other.Id != line.Id && LeadInTiming.Occupancy(other) is { } occupied && Intersects(interval, occupied))
                    || program.LeadIns.Any(cue => cue.Enabled && cue.TargetLineId != line.Id
                        && branch.Lines.FirstOrDefault(other => other.Id == cue.TargetLineId) is { } target
                        && LeadInTiming.Range(target, cue.DurationUs) is { } otherRange && Intersects(interval, otherRange))))
                reason = LeadInCandidateReason.TimingConflict;
            if (settings.ExcludedLineIds.Contains(line.Id)) reason = LeadInCandidateReason.Excluded;
            if (existing is { Enabled: false }) reason = LeadInCandidateReason.Disabled;
            else if (existing?.Origin == LeadInOrigin.Manual) reason = LeadInCandidateReason.ExistingManual;
            LyricLeadIn? proposed = reason != LeadInCandidateReason.None ? null : new()
            {
                Id = existing?.Id ?? "lead-in/" + Uri.EscapeDataString(program.Id) + "/" + Uri.EscapeDataString(line.Id),
                Name = existing?.Name, TargetLineId = line.Id, IndicatorContainerId = node.Id,
                DurationUs = settings.DurationUs, Origin = LeadInOrigin.Generated,
            };
            if (proposed is not null && !LeadInEditor.TrySetLeadIn(configured,program.Id,proposed,out _,out _,manualEdit:false))
            {
                reason = LeadInCandidateReason.TimingConflict;
                proposed = null;
            }
            var status = proposed is not null ? existing is null ? LeadInCandidateStatus.New
                : existing == proposed ? LeadInCandidateStatus.Preserved : LeadInCandidateStatus.Update
                : reason is LeadInCandidateReason.ExistingManual or LeadInCandidateReason.Disabled ? LeadInCandidateStatus.Preserved
                : existing is { Origin: LeadInOrigin.Generated } ? LeadInCandidateStatus.Remove : LeadInCandidateStatus.Skipped;
            candidates.Add(new() { TargetLineId = line.Id, Status = status, Reason = reason, Existing = existing, Proposed = proposed, Range = range, Anchor = anchor });
            if (occupancy is { } known) { previousEnd = Math.Max(previousEnd, known.EndUs); hasKnown = true; unknown = false; }
            else unknown = true;
        }
        return new([.. candidates], ValidationReport.Empty);
    }

    /// <summary>重新验证并一次应用明确选择的差异；不覆盖手工条目或重建提示内容。</summary>
    public static bool TryApply(Project project, string programId, LeadInGenerationSettings settings,
        IReadOnlyCollection<string> selectedLineIds, out Project result, out ValidationReport report)
    {
        var analysis = Analyze(project, programId, settings);
        if (analysis.Report.HasErrors) { result = project; report = analysis.Report; return false; }
        var program = project.LyricPresentation.Programs.First(item => item.Id == programId);
        var cues = program.LeadIns;
        foreach (string id in selectedLineIds)
        {
            var candidate = analysis.Candidates.FirstOrDefault(item => item.TargetLineId == id);
            if (candidate is null || candidate.Status is LeadInCandidateStatus.Preserved or LeadInCandidateStatus.Skipped) continue;
            if (candidate.Existing is { } previous) cues = cues.Remove(previous);
            if (candidate.Proposed is { } proposed) cues = cues.Add(proposed);
        }
        return PresentationEditor.TrySetTree(project, project.LyricPresentation with
        { Programs = project.LyricPresentation.Programs.Replace(program, program with { LeadIns = cues, LeadInGeneration = settings }) }, out result, out report);
    }

    private static bool Intersects(TimeRange first, TimeRange second) => first.StartUs < second.EndUs && second.StartUs < first.EndUs;
    private static ValidationReport Error(string text) => ValidationReport.From([new ProjectIssue
        { Code = DiagnosticCode.InvalidDisplayProgram, Severity = IssueSeverity.Error, Message = text, Location = "project.lyricPresentation.leadInGeneration" }]);
}
