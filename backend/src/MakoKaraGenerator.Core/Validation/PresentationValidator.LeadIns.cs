using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Validation;

public static partial class PresentationValidator
{
    private static ValidationReport ValidateLeadIns(Project project)
    {
        List<ProjectIssue> issues = [];
        var tree = project.LyricPresentation;
        var nodes = tree.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        void Error(string text, string path, DiagnosticCode code = DiagnosticCode.InvalidDisplayProgram) =>
            DiagnosticList.Add(issues, code, IssueSeverity.Error, text, "project.lyricPresentation." + path);
        bool Under(string id, string root)
        {
            HashSet<string> visited = [];
            while (nodes.TryGetValue(id, out var node) && visited.Add(id))
            { if (id == root) return true; if (node.ParentId is null) return false; id = node.ParentId; }
            return false;
        }
        foreach (var node in tree.Nodes)
        {
            string path = "nodes[" + node.Id + "]";
            if (node.Content is not null && node.Layout is not null) Error("内容节点不能同时持有歌词布局。", path);
            if (node.Attachment is { } attachment && (node.Content is not CountdownSequenceContent || !Enum.IsDefined(attachment.Mode)))
                Error("附着只用于前奏提示组，定位方式必须有效。", path + ".attachment");
            switch (node.Content)
            {
                case CountdownSequenceContent sequence:
                    var children = tree.Nodes.Where(child => child.ParentId == node.Id).ToArray();
                    if (sequence.MarkNodeIds.IsEmpty || sequence.MarkNodeIds.Distinct(StringComparer.Ordinal).Count() != sequence.MarkNodeIds.Length
                        || children.Length != sequence.MarkNodeIds.Length
                        || sequence.MarkNodeIds.Any(id => !nodes.TryGetValue(id, out var mark) || mark.ParentId != node.Id || mark.Content is not TextMarkContent))
                        Error("提示组必须包含一个或多个唯一、有序的直接文字标记子节点。", path + ".content.markNodeIds");
                    if (!sequence.Appearance.IsValid) Error("提示默认外观无效。", path + ".content.appearance", DiagnosticCode.InvalidStyleValue);
                    break;
                case TextMarkContent mark:
                    if (string.IsNullOrWhiteSpace(mark.Text) || tree.Nodes.Any(child => child.ParentId == node.Id)
                        || node.ParentId is null || !nodes.TryGetValue(node.ParentId, out var parent)
                        || parent.Content is not CountdownSequenceContent owner || !owner.MarkNodeIds.Contains(node.Id))
                    { Error("标记必须是提示组内有文字的叶节点。", path + ".content"); break; }
                    try { _ = owner.Appearance.Resolve(mark.AppearanceOverride); }
                    catch (ArgumentException exception) { Error(exception.Message, path + ".content.appearanceOverride", DiagnosticCode.InvalidStyleValue); }
                    break;
                case null: break;
                default: Error("未知场景内容类型。", path + ".content"); break;
            }
        }
        HashSet<string> cueIds = [], usedIndicators = [];
        foreach (var program in tree.Programs)
        {
            string path = "programs[" + program.Id + "]";
            var branch = project.Lyrics.Branches.FirstOrDefault(item => item.Id == program.BranchId);
            if (branch is null) continue;
            var policy = program.AnimationPolicy ?? (branch.Role == LyricBranchRole.Primary ? LyricBranchAnimationPolicy.Karaoke : LyricBranchAnimationPolicy.Static);
            HashSet<string> targetIds = [], programIndicators = [];
            List<(LyricLeadIn Cue, TimeRange Range)> ranges = [];
            foreach (var cue in program.LeadIns)
            {
                string cuePath = path + ".leadIns[" + cue.Id + "]";
                var line = branch.Lines.FirstOrDefault(item => item.Id == cue.TargetLineId);
                if (string.IsNullOrWhiteSpace(cue.Id) || !cueIds.Add(cue.Id) || !targetIds.Add(cue.TargetLineId)
                    || line?.Time is not { IsValid: true } || cue.DurationUs <= 0 || !Enum.IsDefined(cue.Origin)
                    || branch.TimingMode != LyricBranchTimingMode.Independent || policy != LyricBranchAnimationPolicy.Karaoke
                    || !Under(cue.IndicatorContainerId, program.RootContainerId)
                    || !nodes.TryGetValue(cue.IndicatorContainerId, out var indicator) || indicator.Content is not CountdownSequenceContent sequence)
                { Error("前奏需要唯一身份、有效目标行、正时长及同程序根下的提示容器。", cuePath); continue; }
                programIndicators.Add(indicator.Id);
                if (!cue.Enabled) continue;
                if (cue.DurationUs < sequence.MarkNodeIds.Length) Error("提示时长不足以形成严格递增的步骤。", cuePath);
                if (LeadInTiming.Range(line, cue.DurationUs) is not { } range)
                { Error("起唱前没有足够时间放入完整前奏，请明确修改时长或禁用。", cuePath, DiagnosticCode.PresentationTimeConflict); continue; }
                ranges.Add((cue, range));
                foreach (var other in branch.Lines.Where(other => other.Id != line.Id))
                    if (LeadInTiming.Occupancy(other) is { } occupied && Intersects(range, occupied))
                        Error("前奏会隐藏仍在演唱的歌词：" + other.Id, cuePath, DiagnosticCode.PresentationTimeConflict);
            }
            foreach (var id in programIndicators)
                if (!usedIndicators.Add(id)) Error("同一个提示容器不能跨显示程序共用，请复制容器。", path);
            for (int i = 0; i < ranges.Count; i++)
                for (int j = i + 1; j < ranges.Count; j++)
                    if (Intersects(ranges[i].Range, ranges[j].Range)) Error("同程序前奏准备区间重叠。", path, DiagnosticCode.PresentationTimeConflict);
            if (program.LeadInGeneration is { } settings)
            {
                if (settings.DurationUs <= 0 || settings.GapThresholdUs < 0
                    || settings.ExcludedLineIds.Distinct(StringComparer.Ordinal).Count() != settings.ExcludedLineIds.Length
                    || settings.ExcludedLineIds.Any(id => !branch.Lines.Any(line => line.Id == id))
                    || settings.IndicatorContainerId is { } id && (!Under(id, program.RootContainerId)
                        || !nodes.TryGetValue(id, out var indicator) || indicator.Content is not CountdownSequenceContent))
                    Error("自动前奏的时长、阈值、排除项或提示容器无效。", path + ".leadInGeneration");
            }
        }
        if (!issues.Any(issue => issue.Severity == IssueSeverity.Error))
            foreach (var program in tree.Programs.Where(program => program.LeadIns.Any(cue => cue.Enabled)))
            {
                var branch = project.Lyrics.Branches.FirstOrDefault(branch => branch.Id == program.BranchId);
                if (branch is null) continue;
                try
                {
                    var schedule = LyricDisplayScheduleBuilder.Build(branch, program, tree);
                    foreach (var cue in program.LeadIns.Where(cue => cue.Enabled))
                    {
                        var target = branch.Lines.First(line => line.Id == cue.TargetLineId);
                        long anchor = LeadInTiming.Anchor(target)!.Value.TimeUs;
                        if (!schedule.Any(entry => entry.Line.Id == target.Id && entry.Contains(anchor)))
                            Error("槽位保留或冲突规则使前奏目标在起唱时不可见，请明确调整布局规则。", "programs[" + program.Id + "].leadIns[" + cue.Id + "]", DiagnosticCode.PresentationTimeConflict);
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException)
                { Error(exception.Message, "programs[" + program.Id + "].leadIns"); }
            }
        return ValidationReport.From(issues);
    }

    private static bool Intersects(TimeRange first, TimeRange second) => first.StartUs < second.EndUs && second.StartUs < first.EndUs;
}
