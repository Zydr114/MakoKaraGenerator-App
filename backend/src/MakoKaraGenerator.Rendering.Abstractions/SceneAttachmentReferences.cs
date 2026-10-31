using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>实际目标文字锚点的唯一位置参考公式，绘制及附着编辑共用。</summary>
public static class SceneAttachmentReferences
{
    /// <summary>为帧内全部提示容器提供真实目标参考；保留父节点固定尺寸，绝不使用 ink 尺寸。</summary>
    public static Dictionary<string, SceneTreeGeometry> Resolve(ProjectSnapshot snapshot, SceneFrame frame, bool includeFixed = false)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        var fixedNodes = SceneTreeGeometryResolver.Resolve(tree.Nodes, snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight);
        Dictionary<string, SceneTreeGeometry> result = new(StringComparer.Ordinal);
        foreach (var cue in frame.LeadIns)
        {
            var group = tree.FindNode(cue.ContainerId)!;
            if (!includeFixed && group.Attachment?.Mode != SceneAttachmentMode.CueTargetText) continue;
            var line = frame.Lines.Single(sample => sample.ElementId == cue.TargetElementId && sample.BindingId == cue.TargetBindingId);
            if (line.Geometry is not { WorldMatrix: { } world } target) continue;
            var parent = group.ParentId is { } parentId ? fixedNodes[parentId]
                : new SceneTreeGeometry(snapshot.Composition.CanvasWidth, snapshot.Composition.CanvasHeight, SceneMatrix.Identity, new(0, 0));
            var anchor = new CanvasPoint(target.TopLeft.X, (target.TopLeft.Y + target.BottomRight.Y) / 2);
            var basis = world * SceneMatrix.Translate(anchor.X, anchor.Y);
            result.Add(group.Id, new(parent.Width, parent.Height, basis, basis.Map(new(0, 0))));
        }
        return result;
    }
}
