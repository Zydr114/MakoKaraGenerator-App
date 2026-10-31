using MakoKaraGenerator.Core.Effects;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>固定应用的共享纯投影，不编辑源工程，也不查询库。</summary>
public static class PresetApplicationsProjection
{
    /// <summary>按工程、分支、场景父链、片段、行、文字解析声明属性。</summary>
    public static LyricStyle ResolveLyrics(Project project, string lineId, string? tokenId = null,
        string? clipId = null, IReadOnlyList<string>? nodePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new PresetStyleResolver(project.LyricStyle, project.Lyrics, project.PresetApplications)
            .ResolveLyrics(lineId, tokenId, clipId, nodePath).Style;
    }

    /// <summary>按真实根到父链解析标记外观；局部外观只覆盖声明属性。</summary>
    public static LyricStyle ResolveMark(Project project, LyricStyle appearance, IReadOnlyList<string> nodePath, string? branchId = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new PresetStyleResolver(project.LyricStyle, project.Lyrics, project.PresetApplications)
            .ResolveMark(appearance, "style", null, "style", nodePath, branchId).Style;
    }

    /// <summary>按祖先先于局部的顺序产生现有执行实例；同一范围配方不展开。</summary>
    public static ProjectEffects Effects(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var instances = project.PresetApplications.Items.Select((item, index) => (item, index))
            .OrderBy(pair => Scope(pair.item.Target.Kind)).ThenBy(pair => NodeDepth(project, pair.item.Target)).ThenBy(pair => pair.index)
            .SelectMany(pair => pair.item.ProjectInstances());
        return new(project.Effects.Packages, project.Effects.Instances.Concat(instances), project.Effects.Budgets);
    }

    private static int Scope(ProjectEffectBindingKind kind) => kind switch
    { ProjectEffectBindingKind.Project => 0, ProjectEffectBindingKind.Branch => 1, ProjectEffectBindingKind.Node => 2,
        ProjectEffectBindingKind.Clips => 3, ProjectEffectBindingKind.Lines => 4, ProjectEffectBindingKind.Tokens => 5, _ => throw new ArgumentOutOfRangeException(nameof(kind)) };
    private static int NodeDepth(Project project, ProjectEffectBinding target)
    {
        if (target.Kind != ProjectEffectBindingKind.Node) return 0;
        int depth = 0; string? id = target.Id; HashSet<string> seen = new(StringComparer.Ordinal);
        while (id is not null && seen.Add(id)) { depth++; id = project.LyricPresentation.FindNode(id)?.ParentId; }
        return depth;
    }
}
