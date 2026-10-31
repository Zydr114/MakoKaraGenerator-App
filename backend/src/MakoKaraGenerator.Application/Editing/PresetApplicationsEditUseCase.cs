#pragma warning disable CS1591
using MakoKaraGenerator.Core.Commands;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Application.Editing;

/// <summary>工程内预设分配的纯准备与单次历史提交出口。模板快照必须由调用方在 Writer 外固定。</summary>
public static class PresetApplicationsEditUseCase
{
    public static PreparedPresetApplicationsEdit PrepareApply(ProjectWorkspace workspace, StyleTemplate snapshot,
        IReadOnlyList<ProjectEffectBinding> targets, IReadOnlyDictionary<string, string>? branchInputs = null)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(targets);
        Project source = workspace.Project;
        if (targets.Count is < 1 or > 1000)
            return Rejected(workspace, source, "presetApplications.apply", "应用目标数量必须在1至1000之间。");
        if (targets.Any(target => target is null) || targets.Select(TargetKey).Distinct().Count() != targets.Count)
            return Rejected(workspace, source, "presetApplications.apply", "应用目标重复或为空。");
        try
        {
            if (!PresetApplicationsEditor.TryApply(source, snapshot, targets, branchInputs, out Project candidate, out string reason))
                return Rejected(workspace, source, "presetApplications.apply", reason);
            return Accepted(workspace, source, candidate, "presetApplications.apply");
        }
        catch (ArgumentException error) { return Rejected(workspace, source, "presetApplications.apply", error.Message); }
    }

    /// <summary>按应用身份更新；只接受同一模板身份，保留实例身份、种子及启停状态。</summary>
    public static PreparedPresetApplicationsEdit PrepareUpdate(ProjectWorkspace workspace, StyleTemplate snapshot,
        IReadOnlyList<string> applicationIds, IReadOnlyDictionary<string, string>? branchInputs = null)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(snapshot); ArgumentNullException.ThrowIfNull(applicationIds);
        Project source = workspace.Project;
        if (applicationIds.Count is < 1 or > 1000 || applicationIds.Any(string.IsNullOrWhiteSpace)
            || applicationIds.Distinct(StringComparer.Ordinal).Count() != applicationIds.Count)
            return Rejected(workspace, source, "presetApplications.update", "应用身份数量或重复值无效。");
        Project candidate = source;
        foreach (string id in applicationIds)
        {
            if (!PresetApplicationsEditor.TryUpdate(candidate, id, snapshot, branchInputs, out candidate, out string reason))
                return Rejected(workspace, source, "presetApplications.update", reason);
        }
        return Accepted(workspace, source, candidate, "presetApplications.update");
    }

    public static PreparedPresetApplicationsEdit PrepareSetEnabled(ProjectWorkspace workspace,
        IReadOnlyList<string> applicationIds, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(applicationIds);
        Project source = workspace.Project;
        if (applicationIds.Count is < 1 or > 1000 || applicationIds.Any(string.IsNullOrWhiteSpace)
            || applicationIds.Distinct(StringComparer.Ordinal).Count() != applicationIds.Count)
            return Rejected(workspace, source, "presetApplications.setEnabled", "应用身份数量或重复值无效。");
        Project candidate = source;
        try
        {
            foreach (string id in applicationIds) candidate = PresetApplicationsEditor.SetEnabled(candidate, id, enabled);
            return Accepted(workspace, source, candidate, "presetApplications.setEnabled");
        }
        catch (ArgumentException error) { return Rejected(workspace, source, "presetApplications.setEnabled", error.Message); }
    }

    public static PreparedPresetApplicationsEdit PrepareRemove(ProjectWorkspace workspace, IReadOnlyList<string> applicationIds)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(applicationIds);
        Project source = workspace.Project;
        if (applicationIds.Count is < 1 or > 1000 || applicationIds.Any(string.IsNullOrWhiteSpace)
            || applicationIds.Distinct(StringComparer.Ordinal).Count() != applicationIds.Count)
            return Rejected(workspace, source, "presetApplications.remove", "应用身份数量或重复值无效。");
        var ids = applicationIds.ToHashSet(StringComparer.Ordinal);
        Project candidate = PresetApplicationsEditor.Replace(source,
            new(source.PresetApplications.Items.Where(item => !ids.Contains(item.Id))));
        return Accepted(workspace, source, candidate, "presetApplications.remove");
    }

    /// <summary>仅在捕获的工作区代次和源引用仍有效时写入一次历史；无变化保留 redo。</summary>
    public static ProjectEditResult CommitPrepared(ProjectWorkspace workspace, PreparedPresetApplicationsEdit prepared)
    {
        ArgumentNullException.ThrowIfNull(workspace); ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(workspace, prepared.Workspace) || workspace.Generation != prepared.Generation)
            return new(false, workspace.Project, 0, "工作区已变化。");
        if (!ReferenceEquals(workspace.Project, prepared.Source)) return new(false, workspace.Project, 0, "工程已变化。");
        if (!prepared.Accepted || !prepared.Changed) return prepared.Result;
        workspace.History.BreakMergeGroup();
        workspace.Execute(ProjectCommand.Create(prepared.CommandName, _ => prepared.Project));
        workspace.History.BreakMergeGroup();
        return prepared.Result;
    }

    private static string TargetKey(ProjectEffectBinding target) => $"{target.Kind}:{target.Id ?? string.Join(",", target.Ids)}:{target.IncludeDescendants}";

    private static PreparedPresetApplicationsEdit Accepted(ProjectWorkspace workspace, Project source, Project project, string name)
        => new(workspace, workspace.Generation, source, project, name, null);
    private static PreparedPresetApplicationsEdit Rejected(ProjectWorkspace workspace, Project source, string name, string reason)
        => new(workspace, workspace.Generation, source, source, name, reason);
}

public sealed class PreparedPresetApplicationsEdit
{
    internal PreparedPresetApplicationsEdit(ProjectWorkspace workspace, long generation, Project source, Project project, string commandName, string? error)
    { Workspace = workspace; Generation = generation; Source = source; Project = project; CommandName = commandName; Error = error; }
    internal ProjectWorkspace Workspace { get; }
    internal long Generation { get; }
    internal Project Source { get; }
    internal Project Project { get; }
    internal string CommandName { get; }
    public string? Error { get; }
    public bool Accepted => Error is null;
    public bool Changed => !ReferenceEquals(Source, Project);
    private static string TargetKey(ProjectEffectBinding target) => $"{target.Kind}:{target.Id ?? string.Join(",", target.Ids)}:{target.IncludeDescendants}";

    public int ChangedApplicationCount
    {
        get
        {
            if (!Changed) return 0;
            var before = Source.PresetApplications.Items.ToDictionary(item => TargetKey(item.Target));
            var after = Project.PresetApplications.Items.ToDictionary(item => TargetKey(item.Target));
            return before.Keys.Union(after.Keys).Count(key => !before.TryGetValue(key, out var a) || !after.TryGetValue(key, out var b) || !a.Equals(b));
        }
    }
    public ProjectEditResult Result => new(Accepted, Project, ChangedApplicationCount, Error);
}

public sealed record ProjectEditResult(bool Accepted, Project Project, int ChangedApplicationCount, string? Error)
{
    public bool Changed => ChangedApplicationCount != 0;
}
