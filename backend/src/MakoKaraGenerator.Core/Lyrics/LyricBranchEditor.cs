using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Lyrics;

/// <summary>分支编辑与单分支行操作之间的纯投影；投影从不持久化或用于渲染。</summary>
public static class LyricBranchEditor
{
    /// <summary>让单分支行编辑操作作用于指定分支，并恢复工程真正的主分支。</summary>
    /// <param name="project">实际工程。</param>
    /// <param name="branchId">正在编辑的分支。</param>
    /// <param name="edit">作用于单分支行视图的纯编辑操作。</param>
    /// <returns>完整工程；无变化或分支不存在时返回原实例。</returns>
    public static Project Edit(Project project, string branchId, Func<Project, Project> edit)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(edit);
        if (!project.Lyrics.Branches.Any(branch => branch.Id == branchId)) return project;
        Project view = branchId == project.Lyrics.PrimaryBranchId ? project
            : project with { Lyrics = project.Lyrics with { PrimaryBranchId = branchId } };
        Project changed = edit(view);
        if (ReferenceEquals(view, changed)) return project;
        LyricsDocument lyrics = changed.Lyrics.PrimaryBranchId == project.Lyrics.PrimaryBranchId ? changed.Lyrics
            : changed.Lyrics with { PrimaryBranchId = project.Lyrics.PrimaryBranchId };
        if (lyrics == project.Lyrics) lyrics = project.Lyrics;
        Project result = changed with { Lyrics = lyrics };
        return result == project ? project : result;
    }

    /// <summary>更新分支参数；无效配置拒绝，无变化返回原实例。</summary>
    /// <param name="project">实际工程。</param>
    /// <param name="branch">包含新参数的分支。</param>
    /// <returns>更新后的工程。</returns>
    public static Project Update(Project project, LyricBranch branch)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(branch);
        if (!branch.IsValid || (branch.Id == project.Lyrics.PrimaryBranchId && branch.Role != LyricBranchRole.Primary)
            || (branch.Id != project.Lyrics.PrimaryBranchId && branch.Role == LyricBranchRole.Primary)) return project;
        try { _ = LyricStyleResolver.Resolve(project.LyricStyle, branchOverride: branch.StyleOverride); }
        catch (ArgumentException) { return project; }
        var lyrics = project.Lyrics.ReplaceBranch(branch);
        return ReferenceEquals(lyrics, project.Lyrics) ? project : project with { Lyrics = lyrics };
    }
}
