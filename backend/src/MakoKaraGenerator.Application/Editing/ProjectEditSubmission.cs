using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Validation;

namespace MakoKaraGenerator.Application.Editing;

// 导入、工程设置与来源管理的原子候选出口；不另持有工作区或历史状态。
internal static class ProjectEditSubmission
{
    internal static bool TryPrepare(Project source, Project candidate, out Project prepared, out string reason)
    {
        prepared = source;
        reason = string.Empty;
        if (candidate == source) return true;
        var presentation = candidate.LyricPresentation.RemoveDeletedSources(candidate.Lyrics);
        if (!ReferenceEquals(presentation, candidate.LyricPresentation)) candidate = candidate with { LyricPresentation = presentation };
        var report = PresentationValidator.Validate(candidate);
        if (report.HasErrors)
        {
            reason = report.Issues.First(issue => issue.Severity == IssueSeverity.Error).Message;
            return false;
        }
        if (candidate == source) return true;
        prepared = candidate;
        return true;
    }

    internal static bool TryExecute(ProjectWorkspace workspace, Project source, Project candidate, string name,
        out Project result, out string reason)
    {
        result = source;
        if (!TryPrepare(source, candidate, out Project prepared, out reason)) return false;
        if (ReferenceEquals(source, prepared)) return true;
        workspace.History.BreakMergeGroup();
        result = workspace.Execute(ProjectCommand.Create(name, _ => prepared));
        workspace.History.BreakMergeGroup();
        return true;
    }
}
