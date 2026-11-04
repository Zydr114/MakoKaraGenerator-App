using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>场景节点只有组和文本；布局策略不改变节点身份。</summary>
public enum SceneElementKind
{
    /// <summary>稳定的显示容器或槽位。</summary>
    Group,
    /// <summary>歌词文本或注音。</summary>
    Text,
}

/// <summary>节点的内部排版策略。</summary>
public enum SceneLocalLayout
{
    /// <summary>普通行局部位置。</summary>
    FreeLine,
    /// <summary>任意数量的稳定槽位。</summary>
    SlotSequence,
    /// <summary>翻译联合测量和安全区流式排布。</summary>
    TranslationFlow,
    /// <summary>注音跟随基础文本。</summary>
    Ruby,
    /// <summary>纯父组或槽位容器。</summary>
    None,
}

/// <summary>画布节点与源数据的关联。</summary>
/// <param name="BranchId">源分支。</param>
/// <param name="LineId">源行。</param>
/// <param name="TokenId">注音基础元素。</param>
/// <param name="RubyId">注音。</param>
public sealed record SceneSourceBinding(string? BranchId, string? LineId = null, string? TokenId = null, string? RubyId = null);

/// <summary>独立于帧采样和后端测量的不可变场景定义。</summary>
public sealed record SceneElementDefinition
{
    /// <summary>稳定画布身份。</summary>
    public required string Id { get; init; }
    /// <summary>父节点身份。</summary>
    public string? ParentId { get; init; }
    /// <summary>节点类型。</summary>
    public SceneElementKind Kind { get; init; }
    /// <summary>源数据关联。</summary>
    public required SceneSourceBinding SourceBinding { get; init; }
    /// <summary>静态位置；空值由文本样式排版默认解析。</summary>
    public ScenePlacement? Placement { get; init; }
    /// <summary>固定参考尺寸。</summary>
    public SceneReferenceRect ReferenceRect { get; init; } = SceneReferenceRect.Canvas;
    /// <summary>内部布局策略。</summary>
    public SceneLocalLayout LocalLayout { get; init; }
    /// <summary>稳定绘制顺序。</summary>
    public int Order { get; init; }
    /// <summary>节点自有内容；源歌词和注音使用 SourceBinding。</summary>
    public SceneContent? Content { get; init; }
    /// <summary>运行时位置参考声明。</summary>
    public SceneAttachment? Attachment { get; init; }
}

/// <summary>可从工程重建的场景树，不写入工程。</summary>
public sealed record SceneDefinition
{
    /// <summary>按绘制顺序排列的节点。</summary>
    public ImmutableArray<SceneElementDefinition> Elements { get; init; } = [];
}

/// <summary>统一场景定义的纯投影。</summary>
public static class SceneDefinitionBuilder
{
    /// <summary>稳定的容器 ID；转义源 ID 避免分隔符碰撞。</summary>
    public static string ContainerId(string branchId) => "branch/" + Uri.EscapeDataString(branchId) + "/display";
    /// <summary>稳定的槽位 ID。</summary>
    public static string SlotId(string branchId, string slotId) => ContainerId(branchId) + "/slot/" + Uri.EscapeDataString(slotId);
    /// <summary>稳定的源文本节点 ID。</summary>
    public static string TextId(string branchId, string lineId) => ContainerId(branchId) + "/text/" + Uri.EscapeDataString(lineId);
    /// <summary>槽位内的文本绑定到固定参考锚点；槽位变换只计算一次。</summary>
    public static ScenePlacement SlotTextPlacement(ScenePlacement slot)
    {
        var anchor = SceneMatrix.Anchor(slot.Anchor, 1, 1);
        return ScenePlacement.Create(new(anchor.X, anchor.Y), slot.Anchor);
    }
    /// <summary>生成当前工程定义；不依赖播放时间。</summary>
    public static SceneDefinition Build(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        List<SceneElementDefinition> elements = [];
        foreach (var node in tree.Nodes)
        {
            var program = tree.Programs.FirstOrDefault(item => item.RootContainerId == node.Id || item.DefaultContainerId == node.Id
                || item.Clips.Any(clip => clip.ContainerId == node.Id)
                || tree.FindNode(item.DefaultContainerId)?.Layout?.SlotIds.Contains(node.Id) == true);
            var branch = snapshot.Lyrics.Branches.FirstOrDefault(source => source.Id == program?.BranchId);
            var layout = node.Layout?.Kind == LyricContainerLayoutKind.SlotSequence ? SceneLocalLayout.SlotSequence
                : node.Layout is null ? SceneLocalLayout.None
                : branch is not null && tree.Resolve(branch.Id).PolicyFor(branch) == LyricBranchAnimationPolicy.Static ? SceneLocalLayout.TranslationFlow : SceneLocalLayout.FreeLine;
            elements.Add(new() { Id = node.Id, ParentId = node.ParentId, Kind = node.Content is TextMarkContent ? SceneElementKind.Text : SceneElementKind.Group,
                SourceBinding = new(program?.BranchId), Placement = node.Placement, ReferenceRect = node.ReferenceRect,
                LocalLayout = layout, Order = elements.Count, Content = node.Content, Attachment = node.Attachment });
        }
        foreach (var branch in snapshot.Lyrics.Branches)
        {
            var program = tree.FindProgram(branch.Id)!;
            var presentation = tree.Resolve(branch.Id);
            foreach (var line in branch.Lines)
            {
                string id = TextId(branch.Id, line.Id);
                var clip = program.Clips.FirstOrDefault(item => item.LineIds.Contains(line.Id));
                elements.Add(new() { Id = id, ParentId = clip?.ContainerId ?? program.DefaultContainerId,
                    Kind = SceneElementKind.Text, SourceBinding = new(branch.Id, line.Id),
                    Placement = clip is not null ? tree.ResolveDisplay(branch.Id, clip.ContainerId).DefaultPlacement ?? ScenePlacement.Default
                        : presentation.DefaultPlacement,
                    LocalLayout = SceneLocalLayout.FreeLine, Order = elements.Count });
                foreach (var ruby in line.RubyAnnotations)
                    elements.Add(new() { Id = id + "/ruby/" + Uri.EscapeDataString(ruby.Id), ParentId = id,
                        Kind = SceneElementKind.Text, SourceBinding = new(branch.Id, line.Id, ruby.BaseTokenId, ruby.Id),
                        LocalLayout = SceneLocalLayout.Ruby, Order = elements.Count });
            }
        }
        return new() { Elements = [.. elements] };
    }

    /// <summary>把采样绑定到场景树，统一容器变换的唯一来源。</summary>
    public static SceneFrame BindFrame(SceneFrame frame, Composition composition)
    {
        var definitions = frame.Definition.Elements.Where(node => node.Kind == SceneElementKind.Group).ToDictionary(node => node.Id, StringComparer.Ordinal);
        var geometry = SceneContainerGeometryResolver.Resolve(frame.Definition, composition);
        return frame with { Lines = [.. frame.Lines.Select(sample =>
        {
            var container = geometry[sample.ContainerId];
            var parent = geometry[sample.ParentId];
            return sample with { ContainerDefinition = definitions[sample.ContainerId], ContainerMatrix = container.WorldMatrix,
                ParentMatrix = parent.WorldMatrix, ParentReferenceWidth = parent.Width, ParentReferenceHeight = parent.Height };
        })] };
    }
}

/// <summary>统一帧；采样后由共享几何后端补齐测量，预览、命中和导出消费同一结果。</summary>
public sealed record SceneFrame
{
    /// <summary>帧对应的定义。</summary>
    public required SceneDefinition Definition { get; init; }
    /// <summary>采样时刻。</summary>
    public long TimeUs { get; init; }
    /// <summary>可见文本及其绑定、动画、逐字状态和测量几何。</summary>
    public ImmutableArray<SceneLineSample> Lines { get; init; } = [];
    /// <summary>当前准备阶段的提示容器装载。</summary>
    public ImmutableArray<SceneLeadInSample> LeadIns { get; init; } = [];
    /// <summary>已整形测量的场景文字标记，不伪造源歌词身份。</summary>
    public ImmutableArray<SceneTextMarkSample> TextMarks { get; init; } = [];
    /// <summary>后端完成测量的节点树，包含容器、槽位、文本和注音。</summary>
    public ImmutableArray<SceneElementFrame> Elements { get; init; } = [];
    /// <summary>共享后端测量的基础字／注音固定绘制族，不重排或修改源数据。</summary>
    public ImmutableArray<Scripting.TokenGeometrySample> TokenGeometries { get; init; } = [];
    /// <summary>完整行的固定整形几何与播放路径，包括无逐字元素的行。</summary>
    public ImmutableArray<Scripting.LineGeometrySample> LineGeometries { get; init; } = [];
    /// <summary>真实文字标记的固定整行几何，没有假源元素。</summary>
    public ImmutableArray<Scripting.TextMarkGeometrySample> MarkGeometries { get; init; } = [];
    /// <summary>真实组参考框／运行矩阵及后代角点，不重建另一套位置规则。</summary>
    public ImmutableArray<Scripting.GroupGeometrySample> GroupGeometries { get; init; } = [];
}

/// <summary>一帧中的场景节点；世界边界只用于读取和命中，不决定容器枢轴。</summary>
public sealed record SceneElementFrame
{
    /// <summary>稳定节点 ID。</summary>
    public required string Id { get; init; }
    /// <summary>装载别名对应的源节点；正常节点为空并使用 Id。</summary>
    public string? SourceNodeId { get; init; }
    /// <summary>本帧父节点 ID。</summary>
    public string? ParentId { get; init; }
    /// <summary>源数据关联。</summary>
    public required SceneSourceBinding SourceBinding { get; init; }
    /// <summary>节点种类。</summary>
    public SceneElementKind Kind { get; init; }
    /// <summary>本帧装载绑定。</summary>
    public string? BindingId { get; init; }
    /// <summary>世界变换。</summary>
    public SceneMatrix WorldMatrix { get; init; } = SceneMatrix.Identity;
    /// <summary>世界枢轴。</summary>
    public CanvasPoint Pivot { get; init; }
    /// <summary>是否拥有本帧可见内容。</summary>
    public bool IsVisible { get; init; }
    /// <summary>动画不透明度，独立于源样式。</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>测量得到的世界边界角点。</summary>
    public ImmutableArray<CanvasPoint> WorldCorners { get; init; } = [];
    /// <summary>节点编辑时的位置参考基底；附着节点使用目标文字局部锚点。</summary>
    public SceneTreeGeometry? PlacementReference { get; init; }
}
