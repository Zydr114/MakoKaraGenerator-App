using System.Collections.Immutable;
using MakoKaraGenerator.Core.Diagnostics;

namespace MakoKaraGenerator.Core.Projects;

/// <summary>前奏制作的不可变编辑，结果统一校验，失败或无变化保留原引用。</summary>
public static class LeadInEditor
{
    /// <summary>单个倒计时标记的默认参考边长（画布像素）。</summary>
    public const double MarkCellSizePx = 40d;

    /// <summary>水平前置提示容器右缘与目标句首字之间的默认间距（画布像素）。</summary>
    public const double DefaultInlineGapPx = 8d;

    /// <summary>首字上方提示容器底缘与目标句注音顶端之间的默认间距（画布像素）。</summary>
    public const double DefaultAboveGapPx = 6d;

    /// <summary>创建紧凑提示容器及等距标记，后续按标记数伸缩尺寸。</summary>
    public static bool TryCreateCountdown(Project project, string parentId, string name, int count, out string containerId,
        out Project result, out ValidationReport report)
    {
        containerId = "node/" + Guid.NewGuid().ToString("N");
        if (count < 1 || project.LyricPresentation.FindNode(parentId) is null)
            return Fail(project, "提示需要有效父组和至少一个标记。", out result, out report);
        var parentReference = SceneTreeGeometryResolver.Resolve(project.LyricPresentation.Nodes,
            project.Composition.CanvasWidth,project.Composition.CanvasHeight)[parentId];
        var ids = Enumerable.Range(0, count).Select(_ => "node/" + Guid.NewGuid().ToString("N")).ToImmutableArray();
        var group = new SceneContainerDefinition { Id = containerId, Name = name, ParentId = parentId,
            ReferenceRect = new(count * MarkCellSizePx / parentReference.Width, MarkCellSizePx / parentReference.Height),
            Placement = ScenePlacement.Create(new(-DefaultInlineGapPx / parentReference.Width, 0d), ScenePlacementAnchor.RightCenter),
            Attachment = new() { Mode = SceneAttachmentMode.CueTargetText },
            Content = new CountdownSequenceContent { MarkNodeIds = ids } };
        var marks = ids.Select((id, index) => new SceneContainerDefinition { Id = id, ParentId = group.Id,
            ReferenceRect = new(1d / count, 1), Content = new TextMarkContent(),
            Placement = ScenePlacement.Create(new((index + .5) / count, .5)) });
        return SceneTreeEditor.TryAddNodes(project, [group, .. marks], out result, out report);
    }

    /// <summary>保存一条前奏；实际手工修改会转为受保护的手工来源。</summary>
    public static bool TrySetLeadIn(Project project, string programId, LyricLeadIn cue, out Project result,
        out ValidationReport report, bool manualEdit = true)
    {
        var tree = project.LyricPresentation;
        int index = Array.FindIndex(tree.Programs.ToArray(), program => program.Id == programId);
        if (index < 0) return Fail(project, "显示程序不存在。", out result, out report);
        var program = tree.Programs[index];
        int cueIndex = Array.FindIndex(program.LeadIns.ToArray(), item => item.Id == cue.Id);
        if (cueIndex >= 0 && program.LeadIns[cueIndex] == cue)
            return PresentationEditor.TrySetTree(project, tree, out result, out report);
        if (manualEdit) cue = cue with { Origin = LeadInOrigin.Manual };
        var cues = cueIndex < 0 ? program.LeadIns.Add(cue) : program.LeadIns.SetItem(cueIndex, cue);
        return PresentationEditor.TrySetTree(project, tree with { Programs = tree.Programs.SetItem(index, program with { LeadIns = cues }) }, out result, out report);
    }

    /// <summary>明确删除前奏，按用户选择记录以后自动跳过；共用容器保留。</summary>
    public static bool TryDeleteLeadIn(Project project, string programId, string cueId, bool excludeFromGeneration,
        out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation;
        int index = Array.FindIndex(tree.Programs.ToArray(), program => program.Id == programId);
        if (index < 0) return Fail(project, "显示程序不存在。", out result, out report);
        var program = tree.Programs[index];
        var cue = program.LeadIns.FirstOrDefault(item => item.Id == cueId);
        if (cue is null) return Fail(project, "前奏不存在。", out result, out report);
        var settings = program.LeadInGeneration;
        if (excludeFromGeneration)
        {
            settings ??= new() { IndicatorContainerId = cue.IndicatorContainerId };
            if (!settings.ExcludedLineIds.Contains(cue.TargetLineId)) settings = settings with { ExcludedLineIds = settings.ExcludedLineIds.Add(cue.TargetLineId) };
        }
        return PresentationEditor.TrySetTree(project, tree with { Programs = tree.Programs.SetItem(index,
            program with { LeadIns = program.LeadIns.Remove(cue), LeadInGeneration = settings }) }, out result, out report);
    }

    /// <summary>重排全部标记引用，不改变各标记的局部位置。</summary>
    public static bool TryReorderMarks(Project project, string containerId, IReadOnlyList<string> orderedIds,
        out Project result, out ValidationReport report)
    {
        var node = project.LyricPresentation.FindNode(containerId);
        if (node?.Content is not CountdownSequenceContent sequence || orderedIds.Count != sequence.MarkNodeIds.Length
            || !orderedIds.Order(StringComparer.Ordinal).SequenceEqual(sequence.MarkNodeIds.Order(StringComparer.Ordinal)))
            return Fail(project, "重排必须包含全部标记且各出现一次。", out result, out report);
        return SceneTreeEditor.TrySetNode(project, node with { Content = sequence with { MarkNodeIds = [.. orderedIds] } }, out result, out report);
    }

    /// <summary>在提示组内追加文字标记，按标记数扩展提示组宽度并保持既有标记局部像素大小与坐标不变。</summary>
    public static bool TryAddMark(Project project, string containerId, string text, out string markId,
        out Project result, out ValidationReport report)
    {
        markId = "node/" + Guid.NewGuid().ToString("N");
        var tree = project.LyricPresentation;
        var group = tree.FindNode(containerId);
        if (group?.Content is not CountdownSequenceContent sequence)
            return Fail(project, "需要选择前奏提示容器。", out result, out report);
        var mark = new SceneContainerDefinition { Id = markId, ParentId = containerId, Content = new TextMarkContent { Text = text },
            ReferenceRect = new(1d / (sequence.MarkNodeIds.Length + 1), 1), Placement = ScenePlacement.Create(new(1d, .5d)) };
        var nodes = ResizeCountdownAfterAppend(tree.Nodes.Add(mark), containerId, markId);
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes }, out result, out report);
    }

    internal static ImmutableArray<SceneContainerDefinition> ResizeCountdownAfterAppend(
        ImmutableArray<SceneContainerDefinition> nodes, string groupId, string appendedMarkId)
    {
        int groupIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == groupId);
        if (groupIndex < 0 || nodes[groupIndex].Content is not CountdownSequenceContent sequence)
            return nodes;
        int oldCount = sequence.MarkNodeIds.Length;
        int newCount = oldCount + 1;
        var group = nodes[groupIndex];
        var groupRect = oldCount > 0
            ? new SceneReferenceRect(group.ReferenceRect.WidthRatio * newCount / oldCount, group.ReferenceRect.HeightRatio)
            : group.ReferenceRect;
        nodes = nodes.SetItem(groupIndex, group with
        {
            ReferenceRect = groupRect,
            Content = sequence with { MarkNodeIds = sequence.MarkNodeIds.Add(appendedMarkId) },
        });
        if (oldCount > 0)
        {
            double ratio = (double)oldCount / newCount;
            foreach (string existingId in sequence.MarkNodeIds)
            {
                int markIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == existingId);
                if (markIndex < 0) continue;
                var existing = nodes[markIndex];
                nodes = nodes.SetItem(markIndex, existing with
                {
                    ReferenceRect = new(existing.ReferenceRect.WidthRatio * ratio, existing.ReferenceRect.HeightRatio),
                    Placement = existing.Placement with { Position = new(existing.Placement.Position.X * ratio, existing.Placement.Position.Y) },
                });
            }
        }
        int appendedIndex = Array.FindIndex(nodes.ToArray(), item => item.Id == appendedMarkId);
        if (appendedIndex >= 0)
        {
            var appended = nodes[appendedIndex];
            nodes = nodes.SetItem(appendedIndex, appended with
            {
                ReferenceRect = new(1d / newCount, appended.ReferenceRect.HeightRatio),
                Placement = appended.Placement with { Position = new((newCount - 0.5d) / newCount, appended.Placement.Position.Y) },
            });
        }
        return nodes;
    }

    /// <summary>用户明确请求等距排列；保留文字、外观与倒计时次序。</summary>
    public static bool TrySpaceMarks(Project project, string containerId, out Project result, out ValidationReport report)
    {
        var tree = project.LyricPresentation;
        if (tree.FindNode(containerId)?.Content is not CountdownSequenceContent sequence)
            return Fail(project, "需要选择前奏提示容器。", out result, out report);
        var nodes = tree.Nodes;
        for (int i = 0; i < sequence.MarkNodeIds.Length; i++)
        {
            int index = Array.FindIndex(nodes.ToArray(), node => node.Id == sequence.MarkNodeIds[i]);
            var node = nodes[index];
            nodes = nodes.SetItem(index, node with { ReferenceRect = new(1d / sequence.MarkNodeIds.Length,1),
                Placement = node.Placement with { Position = new((i+.5)/sequence.MarkNodeIds.Length,.5) } });
        }
        return PresentationEditor.TrySetTree(project, tree with { Nodes = nodes }, out result, out report);
    }

    /// <summary>更新前奏提示容器的统一图形/SVG 与闪烁扫色动画设置。</summary>
    public static bool TrySetCountdownPresentation(Project project, string containerId, CountdownShapeSpec shape,
        CountdownAnimationSpec animation, out Project result, out ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(animation);
        var node = project.LyricPresentation.FindNode(containerId);
        if (node?.Content is not CountdownSequenceContent sequence)
            return Fail(project, "需要选择有效前奏提示容器。", out result, out report);
        return SceneTreeEditor.TrySetNode(project, node with { Content = sequence with { Shape = shape, Animation = animation } },
            out result, out report);
    }

    /// <summary>切换附着方式：固定在父级保持当前画面位置；目标附着自动对齐首字行前或注音上方。</summary>
    public static bool TrySetAttachment(Project project, string containerId, SceneAttachmentMode mode,
        SceneTreeGeometry previous, SceneTreeGeometry newReference, out Project result, out ValidationReport report)
    {
        var node = project.LyricPresentation.FindNode(containerId);
        if (node?.Content is not CountdownSequenceContent || !Enum.IsDefined(mode))
            return Fail(project, "需要有效提示容器及定位方式。", out result, out report);
        if ((node.Attachment?.Mode ?? SceneAttachmentMode.FixedParent) == mode)
            return SceneTreeEditor.TrySetNode(project, node, out result, out report);
        if (!newReference.WorldMatrix.TryInverse(out var inverse) || newReference.Width <= 0 || newReference.Height <= 0)
            return Fail(project, "位置参考无效或矩阵不可逆。", out result, out report);
        var local = inverse * previous.WorldMatrix;
        double scale = Math.Sqrt(local.A * local.A + local.B * local.B);
        var placement = mode switch
        {
            SceneAttachmentMode.FixedParent => BuildFixedParentPlacement(node.Placement, previous, newReference, local, scale),
            SceneAttachmentMode.CueTargetAboveStart => ScenePlacement.Create(
                new(0d, -(previous.Height * scale / 2d + DefaultAboveGapPx) / newReference.Height),
                ScenePlacementAnchor.LeftCenter,
                rotationDegrees: 0d,
                scale: scale),
            _ => ScenePlacement.Create(
                new(-DefaultInlineGapPx / newReference.Width, 0d),
                ScenePlacementAnchor.RightCenter,
                rotationDegrees: 0d,
                scale: scale),
        };
        return SceneTreeEditor.TrySetNode(project, node with
        {
            Attachment = new() { Mode = mode },
            Placement = placement,
            ReferenceRect = new(previous.Width / newReference.Width, previous.Height / newReference.Height),
        }, out result, out report);

        static ScenePlacement BuildFixedParentPlacement(ScenePlacement current, SceneTreeGeometry previous, SceneTreeGeometry newReference,
            SceneMatrix local, double scale)
        {
            var pivot = local.Map(SceneMatrix.Anchor(current.Anchor, previous.Width, previous.Height));
            return current with
            {
                Position = new(pivot.X / newReference.Width, pivot.Y / newReference.Height),
                RotationDegrees = Math.Atan2(local.B, local.A) * 180d / Math.PI,
                Scale = scale,
            };
        }
    }

    private static bool Fail(Project project, string text, out Project result, out ValidationReport report)
    {
        result = project;
        report = ValidationReport.From([new ProjectIssue { Code = DiagnosticCode.InvalidDisplayProgram,
            Severity = IssueSeverity.Error, Message = text, Location = "project.lyricPresentation" }]);
        return false;
    }
}
