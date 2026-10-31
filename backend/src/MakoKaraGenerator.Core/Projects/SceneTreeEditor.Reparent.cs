using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Projects;

public static partial class SceneTreeEditor
{
    /// <summary>保持节点及子树世界外观的重挂；槽位同步移动两边轮换引用。</summary>
    public static bool TryReparent(Project project, string nodeId, string? parentId, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var node = tree.FindNode(nodeId);
        if (node is null || parentId is not null && tree.FindNode(parentId) is null
            || parentId is not null && Descendants(tree, nodeId).Contains(parentId))
            return Fail(project, "新的父组不存在或形成父子循环。", out result, out report);
        if (node.ParentId == parentId) { result = project; report = ValidationReport.Empty; return true; }
        var geometry = SceneTreeGeometryResolver.Resolve(tree.Nodes, project.Composition.CanvasWidth, project.Composition.CanvasHeight);
        var previous = geometry[nodeId];
        var parent = parentId is null ? new SceneTreeGeometry(project.Composition.CanvasWidth, project.Composition.CanvasHeight,
            SceneMatrix.Identity, new(0, 0)) : geometry[parentId];
        if (!parent.WorldMatrix.TryInverse(out var inverse)) return Fail(project, "父组矩阵不可逆。", out result, out report);
        var local = inverse * previous.WorldMatrix;
        double scale = Math.Sqrt(local.A * local.A + local.B * local.B);
        double rotation = Math.Atan2(local.B, local.A) * 180 / Math.PI;
        var anchor = SceneMatrix.Anchor(node.Placement.Anchor, previous.Width, previous.Height);
        var pivot = local.Map(anchor);
        var moved = node with { ParentId = parentId,
            ReferenceRect = new(previous.Width / parent.Width, previous.Height / parent.Height),
            Placement = node.Placement with { Position = new(pivot.X / parent.Width, pivot.Y / parent.Height),
                RotationDegrees = rotation, Scale = scale } };
        var nodes = tree.Nodes;
        var oldSlotOwner = tree.Nodes.FirstOrDefault(owner => owner.Layout?.SlotIds.Contains(nodeId) == true);
        if (oldSlotOwner is not null)
        {
            var newOwner = parentId is null ? null : tree.FindNode(parentId);
            if (newOwner?.Layout is not { Kind: LyricContainerLayoutKind.SlotSequence } newLayout || node.Layout is not null)
                return Fail(project, "槽位只能移入另一槽位序列容器；请先明确调整槽位引用。", out result, out report);
            int oldIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == oldSlotOwner.Id);
            nodes = nodes.SetItem(oldIndex, oldSlotOwner with { Layout = oldSlotOwner.Layout! with
                { SlotIds = oldSlotOwner.Layout!.SlotIds.Remove(nodeId) } });
            int newIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == newOwner.Id);
            nodes = nodes.SetItem(newIndex, newOwner with { Layout = newLayout with { SlotIds = newLayout.SlotIds.Add(nodeId) } });
        }
        int index = Array.FindIndex(nodes.ToArray(), item => item.Id == nodeId);
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes.SetItem(index, moved) }, out result, out report);
    }
}
