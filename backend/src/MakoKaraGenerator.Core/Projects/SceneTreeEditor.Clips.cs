using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Projects;

public static partial class SceneTreeEditor
{
    /// <summary>在普通或槽位布局中设置单句微调；保留本句动画和源数据。</summary>
    public static bool TrySetLocalTransform(Project project, string branchId, string lineId, LyricLocalTransform? local,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var current = tree.FindLine(lineId) ?? new LyricLinePresentation { BranchId = branchId, LineId = lineId };
        if (current.BranchId != branchId || !project.Lyrics.Branches.Any(branch => branch.Id == branchId
            && branch.Lines.Any(line => line.Id == lineId))) return Fail(project, "单句微调的源关联失效。", out result, out report);
        var next = current with { LocalTransform = local };
        int index = Array.FindIndex(tree.LineOverrides.ToArray(), item => item.LineId == lineId);
        bool empty = next.LocalTransform is null && next.EnterAnimation is null;
        var lines = index < 0 ? empty ? tree.LineOverrides : tree.LineOverrides.Add(next)
            : empty ? tree.LineOverrides.RemoveAt(index) : tree.LineOverrides.SetItem(index, next);
        return PresentationEditor.TrySetTree(project, tree with { LineOverrides = lines }, out result, out report);
    }

    /// <summary>新建或更新片段，成员与所有源时间冲突在提交前统一校验。</summary>
    public static bool TrySetClip(Project project, string branchId, LyricDisplayClip clip, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var program = tree.FindProgram(branchId);
        if (program is null) return Fail(project, "源分支没有展示程序。", out result, out report);
        int index = Array.FindIndex(program.Clips.ToArray(), item => item.Id == clip.Id);
        var next = program with { Clips = index < 0 ? program.Clips.Add(clip) : program.Clips.SetItem(index, clip) };
        return SetProgram(project, tree, next, out result, out report);
    }

    /// <summary>删除片段后成员回到默认容器，重新分配与冲突校验属于同一操作。</summary>
    public static bool TryDeleteClip(Project project, string branchId, string clipId, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var program = tree.FindProgram(branchId);
        if (program is null || !program.Clips.Any(clip => clip.Id == clipId)) return Fail(project, "展示片段不存在。", out result, out report);
        return SetProgram(project, tree, program with { Clips = [.. program.Clips.Where(clip => clip.Id != clipId)] }, out result, out report);
    }

    /// <summary>设置明确的默认显示归属，未加入片段的源成员随之重新分配。</summary>
    public static bool TrySetDefaultContainer(Project project, string branchId, string containerId, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var program = tree.FindProgram(branchId);
        if (program is null) return Fail(project, "源分支没有展示程序。", out result, out report);
        return SetProgram(project, tree, program with { DefaultContainerId = containerId }, out result, out report);
    }

    private static bool SetProgram(Project project, LyricPresentationDefinition tree, LyricDisplayProgram program,
        out Project result, out ValidationReport report)
    {
        int index = Array.FindIndex(tree.Programs.ToArray(), item => item.Id == program.Id);
        return PresentationEditor.TrySetTree(project, tree with { Programs = tree.Programs.SetItem(index, program) }, out result, out report);
    }
}
