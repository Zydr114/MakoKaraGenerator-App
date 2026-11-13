using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Rendering;

/// <summary>不可变布局试样；每个 Auto 容器及每个槽位使用独立代表行，不写入工程。</summary>
public static class LayoutReferenceScene
{
    private static readonly ConditionalWeakTable<ProjectSnapshot, ProjectSnapshot> Snapshots = new();
    /// <summary>固定代表文字和中立动画的明确采样版本。</summary>
    public const string Profile = "layout-reference-v1";
    /// <summary>代表文字不宣称继承任何真实行或元素的覆盖。</summary>
    public const string Text = "布局参考 Layout 123";
    /// <summary>布局展开上限，超限拒绝，不截断。</summary>
    public const int MaximumSamples = 512;

    /// <summary>生成保留原分支、节点与预设身份的独立试样快照。</summary>
    public static ProjectSnapshot CreateSnapshot(ProjectSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Snapshots.GetValue(source, static snapshot => Create(snapshot));
    }

    private static ProjectSnapshot Create(ProjectSnapshot source)
    {
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        var additions = source.Lyrics.Branches.ToDictionary(branch => branch.Id, _ => new List<LyricLine>(), StringComparer.Ordinal);
        int count = 0;
        foreach (var node in tree.Nodes.Where(node => node.Layout is not null))
        {
            var branch = Branch(source, tree, node.Id);
            foreach (string parent in node.Layout!.Kind == LyricContainerLayoutKind.SlotSequence ? node.Layout.SlotIds : [node.Id])
            {
                if (++count > MaximumSamples) throw new PreparedFrameException("SceneMeasurementBudgetExceeded", "布局代表行超过完整展开上限。");
                string id = Id(node.Id, parent);
                if (source.Lyrics.AllLines.Any(line => line.Id == id))
                    throw new PreparedFrameException("SceneMeasurementIdentityConflict", "真实行身份与保留布局试样身份冲突。");
                additions[branch.Id].Add(new() { Id = id, Text = Text, Time = new TimeRange(0, 1_000_000), TimingStatus = TimingStatus.Estimated });
            }
        }
        return source with { LyricPresentation = tree, EnterAnimation = null,
            Lyrics = source.Lyrics with { Branches = [.. source.Lyrics.Branches.Select(branch => branch with { Lines = branch.Lines.AddRange(additions[branch.Id]) })] } };
    }

    // 几何保留原工程身份，样式仅为明确生成的试样行切换到同一不可变来源；普通失效行仍由原解析器拒绝。
    internal static ProjectSnapshot StyleSource(ProjectSnapshot source, SceneLineSample sample)
    {
        if (sample.DisplayContainerId is not { } container || sample.ParentNodeId is not { } parent
            || sample.Frame.Line is not { } line || sample.BindingId != Profile + "/" + Uri.EscapeDataString(parent)
            || line.Id != Id(container, parent)) return source;
        var tree = source.LyricPresentation.WithDefaultPrograms(source.Lyrics);
        if (tree.FindNode(container)?.Layout is not { } layout) return source;
        if (layout.Kind == LyricContainerLayoutKind.Auto)
        {
            if (parent != container || sample.SlotId is not null) return source;
        }
        else if (layout.Kind != LyricContainerLayoutKind.SlotSequence || !layout.SlotIds.Contains(parent)
            || sample.SlotId != parent || tree.FindNode(parent) is not { Layout: null } slot || slot.ParentId != container)
            return source;
        var branch = Branch(source, tree, container);
        if (sample.Branch.Id != branch.Id) return source;
        return branch.Lines.Any(candidate => candidate.Id == line.Id) ? source : CreateSnapshot(source);
    }

    /// <summary>对试样快照采样所有布局，完全独立于播放时刻。</summary>
    public static SceneFrame Sample(ProjectSnapshot snapshot)
    {
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        List<SceneLineSample> samples = [];
        foreach (var node in tree.Nodes.Where(node => node.Layout is not null))
        {
            var branch = Branch(snapshot, tree, node.Id);
            var presentation = tree.ResolveDisplay(branch.Id, node.Id);
            var parents = node.Layout!.Kind == LyricContainerLayoutKind.SlotSequence ? node.Layout.SlotIds : [node.Id];
            foreach (string parent in parents)
            {
                var line = branch.Lines.Single(line => line.Id == Id(node.Id, parent));
                samples.Add(new() { Branch = branch, Presentation = presentation, Frame = new() { Line = line, TokensMatchLineText = false },
                    DisplayContainerId = node.Id, ParentNodeId = parent, SlotId = parent == node.Id ? null : parent,
                    BindingId = Profile + "/" + Uri.EscapeDataString(parent),
                    Placement = parent == node.Id ? presentation.DefaultPlacement : SceneDefinitionBuilder.SlotTextPlacement(tree.FindNode(parent)!.Placement) });
            }
        }
        var frame = SceneDefinitionBuilder.BindFrame(new() { Definition = SceneDefinitionBuilder.Build(snapshot), Lines = [.. samples] }, snapshot.Composition);
        List<SceneLeadInSample> cues = [];
        foreach (var node in tree.Nodes.Where(node => node.Content is CountdownSequenceContent))
        {
            var actual = tree.Programs.SelectMany(program => program.LeadIns.Select(cue => (program, cue)))
                .FirstOrDefault(pair => pair.cue.IndicatorContainerId == node.Id);
            string? targetContainer = actual.cue is null ? null : actual.program.Clips.FirstOrDefault(clip => clip.LineIds.Contains(actual.cue.TargetLineId))?.ContainerId
                ?? actual.program?.DefaultContainerId;
            var target = samples.FirstOrDefault(sample => sample.ContainerId == targetContainer) ?? samples.FirstOrDefault();
            if (target is null) continue;
            cues.Add(new() { CueId = actual.cue?.Id ?? Profile + "/cue/" + Uri.EscapeDataString(node.Id),
                ProgramId = actual.program?.Id ?? tree.FindProgram(target.Branch.Id)!.Id, ContainerId = node.Id,
                BranchId = target.Branch.Id, TargetLineId = target.Frame.Line!.Id,
                TargetElementId = target.ElementId, TargetBindingId = target.BindingId!, Range = new TimeRange(0, 1_000_000) });
        }
        return frame with { LeadIns = [.. cues] };
    }

    private static string Id(string container, string parent) => "__layout_reference__/" + Uri.EscapeDataString(container) + "/" + Uri.EscapeDataString(parent);
    private static LyricBranch Branch(ProjectSnapshot snapshot, LyricPresentationDefinition tree, string container)
    {
        var program = tree.Programs.FirstOrDefault(program => program.DefaultContainerId == container || program.Clips.Any(clip => clip.ContainerId == container));
        return snapshot.Lyrics.Branches.FirstOrDefault(branch => branch.Id == program?.BranchId)
            ?? snapshot.Lyrics.PrimaryBranch ?? throw new PreparedFrameException("SceneMeasurementUnavailable", "布局代表模式需要工程主来源。");
    }
}
