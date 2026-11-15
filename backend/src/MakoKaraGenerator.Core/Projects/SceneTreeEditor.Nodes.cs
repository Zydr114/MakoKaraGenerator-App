using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>原生容器与展示片段的不可变编辑，全部提交经过统一校验。</summary>
public static partial class SceneTreeEditor
{
    private static bool Fail(Project project, string message, out Project result, out ValidationReport report)
    {
        result = project;
        report = ValidationReport.From([new ProjectIssue { Code = DiagnosticCode.InvalidDisplayProgram,
            Severity = IssueSeverity.Error, Message = message, Location = "lyricPresentation" }]);
        return false;
    }

    /// <summary>更新指定节点，稳定身份不变；无变化保留原工程。</summary>
    public static bool TrySetNode(Project project, SceneContainerDefinition node, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        int index = Array.FindIndex(tree.Nodes.ToArray(), item => item.Id == node.Id);
        if (index < 0) return Fail(project, "容器不存在。", out result, out report);
        if (tree.Nodes[index] == node) { result = project; report = Validation.PresentationValidator.Validate(project); return !report.HasErrors; }
        return PresentationEditor.TrySetTree(project, tree with { Nodes = tree.Nodes.SetItem(index, node) }, out result, out report);
    }

    /// <summary>设置歌词容器的内部布局默认位置；不改变节点外层位置或参考尺寸。</summary>
    public static bool TrySetLayoutDefaultPlacement(Project project, string containerId, ScenePlacement? placement,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var node = tree.FindNode(containerId);
        if (node?.Layout is not { } layout)
            return Fail(project, "内部布局默认位置需要已有歌词容器。", out result, out report);
        if (placement is { IsValid: false })
            return Fail(project, "内部布局默认位置无效。", out result, out report);
        return TrySetNode(project, node with { Layout = layout with { DefaultPlacement = placement } }, out result, out report);
    }

    /// <summary>加入已明确定义的节点；布局引用须在同一草稿中有效。</summary>
    public static bool TryAddNodes(Project project, IReadOnlyList<SceneContainerDefinition> nodes, out Project result, out ValidationReport report)
        => PresentationEditor.TrySetTree(project, project.LyricPresentation.WithDefaultPrograms(project.Lyrics) with
            { Nodes = project.LyricPresentation.WithDefaultPrograms(project.Lyrics).Nodes.AddRange(nodes) }, out result, out report);

    /// <summary>复制节点子树，重新生成身份及内部引用，不复制源歌词和展示绑定。</summary>
    public static bool TryCopySubtree(Project project, string nodeId, string? name, out string copyId,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        copyId = string.Empty;
        if (tree.FindNode(nodeId) is null) return Fail(project, "容器不存在。", out result, out report);
        var descendants = Descendants(tree, nodeId);
        var ids = descendants.ToDictionary(id => id, _ => "node/" + Guid.NewGuid().ToString("N"), StringComparer.Ordinal);
        copyId = ids[nodeId];
        var copies = tree.Nodes.Where(node => descendants.Contains(node.Id)).Select(node => node with
        {
            Id = ids[node.Id], Name = node.Id == nodeId ? name : node.Name,
            ParentId = node.ParentId is { } parent && ids.TryGetValue(parent, out string? mapped) ? mapped : node.ParentId,
            Layout = node.Layout is not { } layout ? null : layout with { SlotIds = [.. layout.SlotIds.Select(id => ids[id])] },
            Content = node.Content is CountdownSequenceContent sequence
                ? sequence with { MarkNodeIds = [.. sequence.MarkNodeIds.Select(id => ids[id])] } : node.Content,
        });
        var nodes = tree.Nodes.AddRange(copies);
        var owner = tree.Nodes.FirstOrDefault(item => item.Layout?.SlotIds.Contains(nodeId) == true);
        if (owner?.Layout is { } layout)
        {
            int index = Array.FindIndex(nodes.ToArray(), item => item.Id == owner.Id);
            nodes = nodes.SetItem(index, owner with { Layout = layout with { SlotIds = layout.SlotIds.Add(copyId) } });
        }
        var markOwner = tree.Nodes.FirstOrDefault(item => item.Content is CountdownSequenceContent sequence && sequence.MarkNodeIds.Contains(nodeId));
        if (markOwner is not null && !descendants.Contains(markOwner.Id))
            nodes = LeadInEditor.ResizeCountdownAfterAppend(nodes, markOwner.Id, copyId);
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes }, out result, out report);
    }

    private static HashSet<string> Descendants(LyricPresentationDefinition tree, string nodeId)
    {
        HashSet<string> ids = [nodeId];
        while (true)
        {
            int count = ids.Count;
            foreach (var node in tree.Nodes) if (node.ParentId is { } parent && ids.Contains(parent)) ids.Add(node.Id);
            if (count == ids.Count) return ids;
        }
    }

    /// <summary>删除未被展示程序引用的子树；同步清理生成配置中的容器引用并按剩余标记收窄提示容器。</summary>
    public static bool TryDeleteNode(Project project, string nodeId, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        if (tree.FindNode(nodeId) is null) return Fail(project, "容器不存在。", out result, out report);
        var ids = Descendants(tree, nodeId);
        var leadInReferences = tree.Programs.SelectMany(program => program.LeadIns.Where(cue => ids.Contains(cue.IndicatorContainerId))
            .Select(cue => program.BranchId + "／" + cue.TargetLineId + "（" + cue.Id + "）")).ToArray();
        if (leadInReferences.Length > 0)
            return Fail(project, "容器子树仍有前奏引用，请先删除或明确改绑：" + string.Join("、", leadInReferences), out result, out report);
        if (tree.Programs.Any(program => ids.Contains(program.RootContainerId) || ids.Contains(program.DefaultContainerId)
            || program.Clips.Any(clip => ids.Contains(clip.ContainerId))
            || program.LeadIns.Any(cue => ids.Contains(cue.IndicatorContainerId))))
            return Fail(project, "容器子树仍有默认归属、片段或前奏引用，请先明确改绑。", out result, out report);
        var oldCounts = tree.Nodes
            .Where(node => !ids.Contains(node.Id) && node.Content is CountdownSequenceContent)
            .ToDictionary(node => node.Id, node => ((CountdownSequenceContent)node.Content!).MarkNodeIds.Length, StringComparer.Ordinal);
        var nodes = tree.Nodes.Where(node => !ids.Contains(node.Id)).Select(node => node with
        {
            Layout = node.Layout is not { } layout ? null : layout with { SlotIds = [.. layout.SlotIds.Where(id => !ids.Contains(id))] },
            Content = node.Content is CountdownSequenceContent sequence
                ? sequence with { MarkNodeIds = [.. sequence.MarkNodeIds.Where(id => !ids.Contains(id))] } : node.Content,
        }).ToImmutableArray();
        nodes = ShrinkCountdownsAfterDelete(nodes, oldCounts);
        var programs = tree.Programs.Select(program =>
            program.LeadInGeneration is { IndicatorContainerId: { } indicator } settings && ids.Contains(indicator)
                ? program with { LeadInGeneration = settings with { IndicatorContainerId = null } }
                : program).ToImmutableArray();
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes, Programs = programs }, out result, out report);
    }

    private static ImmutableArray<SceneContainerDefinition> ShrinkCountdownsAfterDelete(
        ImmutableArray<SceneContainerDefinition> nodes, Dictionary<string, int> oldCounts)
    {
        for (int ownerIndex = 0; ownerIndex < nodes.Length; ownerIndex++)
        {
            var owner = nodes[ownerIndex];
            if (owner.Content is not CountdownSequenceContent sequence
                || !oldCounts.TryGetValue(owner.Id, out int oldCount))
                continue;
            int newCount = sequence.MarkNodeIds.Length;
            if (newCount < 1 || newCount >= oldCount) continue;
            nodes = nodes.SetItem(ownerIndex, owner with
            {
                ReferenceRect = new(owner.ReferenceRect.WidthRatio * newCount / oldCount, owner.ReferenceRect.HeightRatio),
            });
            for (int newIndex = 0; newIndex < newCount; newIndex++)
            {
                string markId = sequence.MarkNodeIds[newIndex];
                int markIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == markId);
                if (markIndex < 0) continue;
                var mark = nodes[markIndex];
                nodes = nodes.SetItem(markIndex, mark with
                {
                    ReferenceRect = new(1d / newCount, mark.ReferenceRect.HeightRatio),
                    Placement = mark.Placement with { Position = new((newIndex + 0.5d) / newCount, mark.Placement.Position.Y) },
                });
            }
        }
        return nodes;
    }

    /// <summary>在现有槽位序列加入一个新槽位，不隐式修改同屏容量。</summary>
    public static bool TryAddSlot(Project project, string containerId, SceneContainerDefinition slot,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var container = tree.FindNode(containerId);
        if (container?.Layout is not { Kind: LyricContainerLayoutKind.SlotSequence } layout
            || slot.ParentId != containerId || slot.Layout is not null)
            return Fail(project, "新增槽位需要槽位容器及有效的直接子节点。", out result, out report);
        int index = Array.FindIndex(tree.Nodes.ToArray(), node => node.Id == containerId);
        var nodes = tree.Nodes.SetItem(index, container with { Layout = layout with { SlotIds = layout.SlotIds.Add(slot.Id) } }).Add(slot);
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes }, out result, out report);
    }

    /// <summary>明确重排全部槽位，整段轮换将按新次序从首槽重建。</summary>
    public static bool TryReorderSlots(Project project, string containerId, IReadOnlyList<string> orderedIds,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var node = tree.FindNode(containerId);
        if (node?.Layout is not { Kind: LyricContainerLayoutKind.SlotSequence } layout
            || orderedIds.Count != layout.SlotIds.Length
            || !orderedIds.Order(StringComparer.Ordinal).SequenceEqual(layout.SlotIds.Order(StringComparer.Ordinal)))
            return Fail(project, "重排必须包含全部槽位且每个出现一次。", out result, out report);
        return TrySetNode(project, node with { Layout = layout with { SlotIds = [.. orderedIds] } }, out result, out report);
    }

    /// <summary>重排树中同级显示次序，不改变槽位轮换次序或媒体绘制次序。</summary>
    public static bool TryReorderChildren(Project project, string? parentId, IReadOnlyList<string> orderedIds,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation.WithDefaultPrograms(project.Lyrics);
        var children = tree.Nodes.Where(node => node.ParentId == parentId).ToArray();
        if (!orderedIds.Order(StringComparer.Ordinal).SequenceEqual(children.Select(node => node.Id).Order(StringComparer.Ordinal)))
            return Fail(project, "同级重排必须包含全部子节点且每个出现一次。", out result, out report);
        var replacements = orderedIds.Select(id => children.First(node => node.Id == id)).ToArray();
        int index = 0;
        var nodes = tree.Nodes.Select(node => node.ParentId == parentId ? replacements[index++] : node).ToImmutableArray();
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes }, out result, out report);
    }
}
