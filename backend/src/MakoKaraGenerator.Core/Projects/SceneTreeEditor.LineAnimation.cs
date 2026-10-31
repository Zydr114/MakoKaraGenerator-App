using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Projects;

public static partial class SceneTreeEditor
{
    /// <summary>用稳定来源与行身份设置进入动画，保留局部变换；null恢复工程继承。</summary>
    public static bool TrySetLineEnterAnimation(Project project, string branchId, string lineId, LineAnimation? animation,
        out Project result, out ValidationReport report)
    {
        if (animation is { IsValid: false } || !project.Lyrics.Branches.Any(branch => branch.Id == branchId && branch.Lines.Any(line => line.Id == lineId)))
            return Fail(project, "本句动画参数或源关联无效。", out result, out report);
        var tree = project.LyricPresentation;
        var current = tree.FindLine(lineId);
        if (current is not null && current.BranchId != branchId)
            return Fail(project, "本句动画的源关联失效。", out result, out report);
        if (current?.EnterAnimation == animation)
        { result = project; report = Validation.PresentationValidator.Validate(project); return !report.HasErrors; }
        var next = (current ?? new LyricLinePresentation { BranchId = branchId, LineId = lineId }) with { EnterAnimation = animation };
        int index = Array.FindIndex(tree.LineOverrides.ToArray(), item => item.LineId == lineId);
        var lines = index < 0 ? tree.LineOverrides.Add(next) : next.LocalTransform is null && animation is null
            ? tree.LineOverrides.RemoveAt(index) : tree.LineOverrides.SetItem(index, next);
        return PresentationEditor.TrySetTree(project, tree with { LineOverrides = lines }, out result, out report);
    }
}
