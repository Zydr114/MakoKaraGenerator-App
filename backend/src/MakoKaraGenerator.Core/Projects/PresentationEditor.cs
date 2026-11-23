namespace MakoKaraGenerator.Core.Projects;

/// <summary>容器与内部展示的不可变编辑；无变化返回源工程。</summary>
public static class PresentationEditor
{
    /// <summary>更新分支展示，校验结构和源关联。</summary>
    public static Project SetBranch(Project project, LyricBranchPresentation definition)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(definition);
        var branch = project.Lyrics.Branches.FirstOrDefault(item => item.Id == definition.BranchId);
        if (branch is null || !definition.IsValid
            || (definition.SlotSequenceProfile is not null && definition.PolicyFor(branch) != Lyrics.LyricBranchAnimationPolicy.Karaoke)
            || definition.Lines.Any(line => !branch.Lines.Any(source => source.Id == line.LineId))) return project;
        var presentation = project.LyricPresentation.SetBranch(definition);
        if (ReferenceEquals(presentation, project.LyricPresentation)) return project;
        var result = project with { LyricPresentation = presentation };
        return Validation.PresentationValidator.Validate(result).HasErrors ? project : result;
    }

    /// <summary>预演完整原生展示树；失败返回原工程和具体诊断，相同配置返回原引用。</summary>
    public static bool TrySetTree(Project project, LyricPresentationDefinition tree, out Project result, out Diagnostics.ValidationReport report)
    {
        var candidate = project.LyricPresentation == tree ? project : project with { LyricPresentation = tree };
        report = Validation.PresentationValidator.Validate(candidate);
        result = report.HasErrors ? project : candidate;
        return !report.HasErrors;
    }

    /// <summary>更新内部行定义；清空覆盖时移除该条目。</summary>
    public static LyricBranchPresentation SetLine(LyricBranchPresentation branch, string lineId, LyricLinePresentation line)
    {
        int index = Array.FindIndex(branch.Lines.ToArray(), item => item.LineId == lineId);
        bool empty = line.LocalTransform is null;
        if (index < 0 && empty || index >= 0 && branch.Lines[index] == line) return branch;
        return branch with { Lines = empty ? branch.Lines.RemoveAt(index)
            : index < 0 ? branch.Lines.Add(line) : branch.Lines.SetItem(index, line) };
    }

    /// <summary>复制源行时复制其内部展示覆盖；容器保持不变。</summary>
    public static Project CopyLine(Project project, string branchId, string sourceId, string targetId)
    {
        var branch = project.LyricPresentation.Resolve(branchId);
        if (branch.FindLine(sourceId) is not { } source) return project;
        return SetBranch(project, SetLine(branch, targetId, source with { LineId = targetId }));
    }
}
