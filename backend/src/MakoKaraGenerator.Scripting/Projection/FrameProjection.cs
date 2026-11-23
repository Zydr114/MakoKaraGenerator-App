using System.Collections.Immutable;
using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>共享 SceneFrame 的中立脚本输入投影，不再生成另一套显示调度。</summary>
public static class FrameProjection
{
    /// <summary>当前提示真实文字及目标句；标记不持有源字或 occurrence。</summary>
    public static ImmutableArray<FrameMark> Marks(SceneFrame scene, SceneCatalog catalog)
    {
        var lines = Lines(scene, catalog).ToDictionary(static line => line.BindingId);
        return [.. scene.TextMarks.Select(mark => new FrameMark(new(new(mark.NodeId), new(mark.CueId), new(mark.Geometry.BindingId!)),
            lines[new(mark.Geometry.BindingId!)], new(mark.ContainerId), mark.Text, mark.IsElapsed, mark.Opacity))];
    }
    /// <summary>真实组运行父树及可见性；不把持久父关系覆盖成装载关系。</summary>
    public static ImmutableArray<FrameGroup> Groups(SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return [.. scene.Elements.Where(static node => node.Kind == SceneElementKind.Group && node.SourceNodeId is null).Select(node => new FrameGroup(new(new(node.Id)),
            node.ParentId is { } parent ? new(parent) : null, node.IsVisible, node.Opacity))];
    }
    /// <summary>真实源行及当前父槽位装载；未知演唱时间不变成假起唱。</summary>
    public static ImmutableArray<FrameLine> Lines(SceneFrame scene, SceneCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(catalog);
        return [.. scene.Lines.Select(line => new FrameLine(new(line.Frame.Line!.Id), new(line.Branch.Id), new(line.BindingId!),
            new(line.SourceElementId), new(line.ExitReplica is { SourceNodePath.Length: >= 2 } exit ? exit.SourceNodePath[^2] : line.ParentId), catalog.Programs.Single(program => program.BranchId.Value == line.Branch.Id).Id,
            line.DisplayClipId is { } clip ? new(clip) : null, line.CueId is { } cue ? new(cue) : null,
            line.DisplayPhase == SceneDisplayPhase.Preparation ? DisplayPhase.Preparing
                : line.Frame.Line.Time is { } range && scene.TimeUs >= range.EndUs ? DisplayPhase.Retained : DisplayPhase.Performing,
            1d))];
    }
    /// <summary>本帧所有真实源元素；输出绑定由实例再筛选，提示标记无假源元素。</summary>
    public static ImmutableArray<FrameToken> Tokens(SceneFrame scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        List<FrameToken> tokens = [];
        foreach (SceneLineSample line in scene.Lines)
        {
            var source = line.Frame.Line!;
            BranchId branchId = new(line.Branch.Id); LineId lineId = new(source.Id);
            BindingId bindingId = new(line.BindingId!);
            // 演唱输入来自同一个 LyricSampler；Static／FollowPrimary 的绘制状态可能刻意清零。
            // 该视觉策略不能抹去源区间或将静态对象假装为未定时。
            LyricFrameSample performanceFrame = LyricSampler.SampleLine(source, scene.TimeUs);
            foreach (TokenSample sampled in performanceFrame.Tokens)
            {
                var token = source.Tokens[sampled.Index];
                TokenId tokenId = new(token.Id);
                var performance = ContentProjection.TokenPerformance(token, sampled, scene.TimeUs, performanceFrame.TokensMatchLineText);
                tokens.Add(new() { TokenId = tokenId, BranchId = branchId, LineId = lineId, BindingId = bindingId,
                    OccurrenceId = performance.TimeRange is { } range
                        ? StableIdentities.Occurrence(branchId, lineId, tokenId, range) : null,
                    SourceText = token.Text, Performance = performance,
                    WithRuby = new(tokenId, bindingId), BaseOnly = new(tokenId, bindingId) });
            }
        }
        return [.. tokens];
    }
}
