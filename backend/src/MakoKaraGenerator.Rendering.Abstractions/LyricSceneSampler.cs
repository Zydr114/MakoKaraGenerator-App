using System.Collections.Immutable;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>场景中的可见行；不包含颜色或编辑器状态。</summary>
public sealed record SceneLineSample
{
    /// <summary>所属分支。</summary>
    public required LyricBranch Branch { get; init; }
    /// <summary>由统一时间采样器生成的逐字状态。</summary>
    public required LyricFrameSample Frame { get; init; }
    /// <summary>当前展示动画策略。</summary>
    public LyricBranchAnimationPolicy AnimationPolicy => Presentation.PolicyFor(Branch);
    /// <summary>所属稳定容器 ID。</summary>
    public string ContainerId => DisplayContainerId ?? LyricSceneIds.Regular(Branch.Id);
    /// <summary>显示程序指定的容器；空值使用默认常规容器。</summary>
    public string? DisplayContainerId { get; init; }
    /// <summary>稳定源文本节点 ID。</summary>
    public string ElementId => RuntimeElementId ?? SourceElementId;
    /// <summary>原始源文字节点身份；退出副本不会制造另一个源节点。</summary>
    public string SourceElementId => SceneDefinitionBuilder.TextId(Branch.Id, Frame.Line!.Id);
    /// <summary>仅绘制父树使用的装载别名；正常源装载为空。</summary>
    public string? RuntimeElementId { get; init; }
    /// <summary>隐藏左极限冻结的退出副本身份；不参与源布局、事件或播放头。</summary>
    public ExitReplicaMetadata? ExitReplica { get; init; }
    /// <summary>帧中的实际父节点；双行文本绑定到稳定槽位。</summary>
    public string ParentId => ParentNodeId ?? (SlotId is { } slotId ? SceneDefinitionBuilder.SlotId(Branch.Id, slotId) : ContainerId);
    /// <summary>实际父节点；槽位引用原生树的稳定节点 ID。</summary>
    public string? ParentNodeId { get; init; }
    /// <summary>场景定义中的外层容器。</summary>
    public SceneElementDefinition? ContainerDefinition { get; init; }
    /// <summary>从场景定义计算的父容器世界矩阵。</summary>
    public SceneMatrix ContainerMatrix { get; init; } = SceneMatrix.Identity;
    /// <summary>文本父节点的世界矩阵。</summary>
    public SceneMatrix ParentMatrix { get; init; } = SceneMatrix.Identity;
    /// <summary>文本父节点的固定局部参考宽度。</summary>
    public double ParentReferenceWidth { get; init; }
    /// <summary>文本父节点的固定局部参考高度。</summary>
    public double ParentReferenceHeight { get; init; }
    /// <summary>独占片段身份；常规采样为空。</summary>
    public string? DisplayClipId { get; init; }
    /// <summary>本次槽位装载的身份。</summary>
    public string? BindingId { get; init; }
    /// <summary>当前展示定义。</summary>
    public required LyricBranchPresentation Presentation { get; init; }
    /// <summary>统一后端测量的最终几何。</summary>
    public LineRenderGeometry? Geometry { get; init; }
    /// <summary>动态槽位标识；静态分支为空。</summary>
    public string? SlotId { get; init; }
    /// <summary>父容器内的生效局部位置，不含外层变换。</summary>
    public ScenePlacement? Placement { get; init; }
    /// <summary>源行在当前槽位或容器中的微调。</summary>
    public LyricLocalTransform? LocalTransform { get; init; }
    /// <summary>动画通道值。</summary>
    public SceneTransform Transform { get; init; } = SceneTransform.Neutral;
    /// <summary>进入阶段进度。</summary>
    public double EnterProgress { get; init; } = 1;
    /// <summary>退出阶段进度。</summary>
    public double ExitProgress { get; init; }
    /// <summary>准备显示与正常演唱明确区分。</summary>
    public SceneDisplayPhase DisplayPhase { get; init; }
    /// <summary>本句装载关联的前奏身份，起唱后继续保留。</summary>
    public string? CueId { get; init; }
}

/// <summary>分支与动态布局的纯时间重建；预览和导出调用同一入口。</summary>
public static partial class LyricSceneSampler
{
    /// <summary>采样全部分支的可见行。</summary>
    /// <param name="snapshot">只读工程快照。</param>
    /// <param name="timeUs">目标时刻。</param>
    /// <returns>按分支和行顺序排列的场景。</returns>
    public static ImmutableArray<SceneLineSample> Sample(ProjectSnapshot snapshot, long timeUs) => SampleFrame(snapshot, timeUs).Lines;

    /// <summary>重建统一场景帧。</summary>
    public static SceneFrame SampleFrame(ProjectSnapshot snapshot, long timeUs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var identities = NeedsLoadIdentities(snapshot) ? LoadIdentities(snapshot, cancellationToken) : null;
        snapshot = snapshot with { LyricPresentation = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics) };
        var frame = SceneDefinitionBuilder.BindFrame(new() { Definition = SceneDefinitionBuilder.Build(snapshot),
            TimeUs = timeUs, Lines = identities is null ? SampleLines(snapshot, timeUs)
                : identities.Apply(SampleLines(snapshot, timeUs), timeUs) }, snapshot.Composition);
        return frame with { LeadIns = SampleLeadIns(snapshot, frame) };
    }

    private static ImmutableArray<SceneLineSample> SampleLines(ProjectSnapshot snapshot, long timeUs)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        List<SceneLineSample> result = [];
        LyricBranch? primary = snapshot.Lyrics.PrimaryBranch;
        if (primary is null) return [];
        var primaryScene = SampleIndependent(snapshot, primary, timeUs);
        foreach (LyricBranch branch in snapshot.Lyrics.Branches)
        {
            if (branch.Id == primary.Id) { result.AddRange(primaryScene); continue; }
            foreach (SceneLineSample sample in SampleIndependent(snapshot, branch, timeUs))
                if (!(branch.TimingMode == LyricBranchTimingMode.FollowPrimary && sample.Frame.Line?.PrimaryLineId is not null))
                    result.Add(sample);
            if (branch.TimingMode != LyricBranchTimingMode.FollowPrimary) continue;
            for (int i = 0; i < branch.Lines.Length; i++)
            {
                LyricLine line = branch.Lines[i];
                SceneLineSample? associated = primaryScene.FirstOrDefault(entry => entry.Frame.Line?.Id == line.PrimaryLineId);
                if (line.PrimaryLineId is null || associated is null) continue;
                SceneLineSample following = CreateSample(snapshot, branch, LyricSampler.SampleLine(line, timeUs, i)) with
                    { DisplayPhase = associated.DisplayPhase };
                if (following.AnimationPolicy == LyricBranchAnimationPolicy.FollowPrimary)
                    following = following with { Transform = associated.Transform,
                        EnterProgress = associated.EnterProgress, ExitProgress = associated.ExitProgress };
                result.Add(following);
            }
        }
        return [.. result];
    }

    private static ImmutableArray<SceneLineSample> SampleIndependent(ProjectSnapshot snapshot, LyricBranch branch, long timeUs)
    {
        if (branch.TimingMode == LyricBranchTimingMode.None) return [];
        var program = snapshot.LyricPresentation.FindProgram(branch.Id)!;
        if (program.LeadIns.Any(cue => cue.Enabled) || program.Clips.Any(clip => clip.ScheduleContext is not null))
            return SamplePreparedProgram(snapshot, branch, program, timeUs);
        var segment = LyricDisplaySegmentResolver.Resolve(branch, program, timeUs);
        string containerId = segment.ContainerId(program);
        var presentation = snapshot.LyricPresentation.ResolveDisplay(branch.Id, containerId);
        string Binding(string parentId, string lineId) => Uri.EscapeDataString(program.Id) + "/binding/"
            + Uri.EscapeDataString(segment.Identity) + "/" + Uri.EscapeDataString(parentId) + "/" + Uri.EscapeDataString(lineId);
        if (presentation.PolicyFor(branch) == LyricBranchAnimationPolicy.Karaoke && presentation.SlotSequenceProfile is { IsValid: true } profile)
            return [.. SlotSequenceAnimationScheduler.Sample(segment.Lines, timeUs, profile)
                .Select(entry => CreateSlotSample(snapshot, branch, segment, presentation, profile, entry, timeUs))];
        LyricFrameSample frame = LyricSampler.Sample(new LyricsDocument { Lines = segment.Lines }, timeUs);
        if (!frame.HasLine) return [];
        frame = frame with { LineIndex = LyricDisplaySegmentResolver.IndexOf(branch, frame.Line!.Id) };
        SceneLineSample sample = CreateSample(snapshot, branch, frame) with
        { DisplayContainerId = containerId, Presentation = presentation, DisplayClipId = segment.Clip?.Id,
            Placement = presentation.DefaultPlacement,
            BindingId = Binding(containerId, frame.Line!.Id) };
        if (presentation.PolicyFor(branch) == LyricBranchAnimationPolicy.Karaoke) sample = ApplyLineAnimation(snapshot, sample, timeUs);
        return [sample];
    }

    private static SceneLineSample ApplyLineAnimation(ProjectSnapshot snapshot, SceneLineSample sample, long timeUs, long? revealTimeUs = null)
    {
        var values = LineAnimationEvaluator.Evaluate(LineAnimationEditor.Resolve(snapshot.EnterAnimation,
            snapshot.LyricPresentation, sample.Frame.Line!), revealTimeUs ?? sample.Frame.Line!.Time?.StartUs ?? 0, timeUs);
        return values is not { } animation ? sample : sample with { Transform = new()
            { Opacity = animation.Opacity, Scale = animation.Scale, OffsetYPx = animation.VerticalOffsetPx } };
    }

    private static SceneLineSample CreateSample(ProjectSnapshot snapshot, LyricBranch branch, LyricFrameSample frame)
    {
        var program = snapshot.LyricPresentation.FindProgram(branch.Id)!;
        var segment = LyricDisplaySegmentResolver.Resolve(branch, program, frame.TimeUs);
        return new()
        {
        BindingId = BindingId(program, segment, program.DefaultContainerId, frame.Line!.Id),
        LocalTransform = snapshot.LyricPresentation.FindLine(frame.Line!.Id)?.LocalTransform,
        Branch = branch, DisplayContainerId = snapshot.LyricPresentation.FindProgram(branch.Id)!.DefaultContainerId, Presentation = snapshot.LyricPresentation.Resolve(branch.Id), Frame = snapshot.LyricPresentation.Resolve(branch.Id).PolicyFor(branch) != LyricBranchAnimationPolicy.Karaoke ? frame with
        {
            LineProgress = 0, CurrentTokenIndex = -1,
            Tokens = [.. frame.Tokens.Select(token => token with { Progress = 0, IsCurrent = false, Source = ProgressSource.Unavailable, Time = null })],
            RubySegments = [.. frame.RubySegments.Select(segment => segment with { Progress = 0, IsCurrent = false, Source = ProgressSource.Unavailable, Time = null })],
        } : frame,
        Placement = snapshot.LyricPresentation.Resolve(branch.Id).DefaultPlacement,
        };
    }
    private static double Ratio(long elapsed, long duration) => duration <= 0 ? 1 : Math.Clamp((double)elapsed / duration, 0, 1);
}
