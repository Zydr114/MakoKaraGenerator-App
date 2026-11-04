using System.Collections.Immutable;
using MakoKaraGenerator.Scripting.V2;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class V2SdkContractTests
{
    [Fact]
    public void 准备结果允许自定义只读记录且帧不公开旧状态句柄()
    {
        PreparedEffect data = new TestPrepared([1, 2, 3]);
        Assert.Equal(3, Assert.IsType<TestPrepared>(data).Ordinals.Length);
        Assert.Same(PreparedEffect.Empty, PreparedEffect.Empty);
        Assert.DoesNotContain(typeof(TextFrame).GetProperties(), property =>
            property.PropertyType == typeof(FrameContext) || property.PropertyType == typeof(FrameWriter)
            || property.PropertyType == typeof(TimelineHistory) || property.PropertyType == typeof(GeometryQueries)
            || property.Name is "History" or "LastSimulationBoundaryUs" or "State" or "Diagnostics");
        Assert.All(typeof(DeclareContext).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.All(typeof(TextLineLoad).GetProperties(), property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void 真实事件按类型范围和原文索引且重复装载不增加occurrence序号()
    {
        var first = Start("first", "branch-a", "line-a", 100, 0);
        var second = Start("second", "branch-b", "line-b", 200, 1);
        var third = Start("third", "branch-a", "line-a", 300, 0);
        TimelineEvent[] raw = [third, second, first, first.WithReplayReason(ReplayReason.Seek), new TokenCompleted(first.Source, first.SourceOrder, ReplayReason.Initial)];
        var index = new TimelineIndex(raw);
        var all = new TimelineSelector(TimelineEventKind.TokenStarted, sourceText: "repeat");
        var branch = new TimelineSelector(TimelineEventKind.TokenStarted, new TimelineScope.Branch(new("branch-a")), "repeat");
        Assert.Equal(4, index.Events.Length);
        Assert.Equal(3, index.Count(all)); Assert.Equal(2, index.Count(branch));
        Assert.Equal(1, index.Ordinal(all, first.OccurrenceId));
        Assert.Equal(2, index.Ordinal(all, second.OccurrenceId));
        Assert.Equal(3, index.Ordinal(all, third.OccurrenceId));
        Assert.Equal(2, index.Ordinal(branch, third.OccurrenceId));
        Assert.Null(index.Ordinal(branch, second.OccurrenceId));
        Assert.Equal(first.EventId, index.Nth(all, 0)!.EventId);
        Assert.Equal(third.EventId, index.Nth(all, 2)!.EventId);
        Assert.Null(index.Nth(all, 3));
        Assert.Equal(0, index.Count(new(TimelineEventKind.BindingEntered)));
        Assert.Equal(0, index.Count(new(TimelineEventKind.TokenStarted, sourceText: "Repeat")));
    }

    [Fact]
    public void 事件边界前后一微秒及乱序查询都使用相同半开结果()
    {
        var start = Start("first", "branch-a", "line-a", 100, 0);
        var completed = new TokenCompleted(start.Source, start.SourceOrder, ReplayReason.Initial);
        var next = Start("next", "branch-a", "line-a", completed.TimeUs, 0);
        var index = new TimelineIndex([next, start, completed]);
        var selector = new TimelineSelector(TimelineEventKind.TokenStarted);
        long[] queries = [100, 99, 101, 100, 199, 200, 201, 99];
        int[] counts = [1, 0, 1, 1, 1, 2, 2, 0];
        Assert.Equal(counts, queries.Select(time => index.Count(selector, time)));
        Assert.Null(index.Last(selector, 99));
        Assert.Equal(start.EventId, index.Last(selector, 100)!.EventId);
        Assert.Equal(start.EventId, index.Next(selector, 99)!.EventId);
        Assert.Equal(next.EventId, index.Next(selector, 100)!.EventId);
        Assert.Null(index.Next(selector, 200));
        Assert.Equal(1, index.Count(selector, new TimeRange(100, 200)));
        Assert.Equal([start.EventId], index.GetRange(selector, new(100, 200)).Select(item => item.EventId));
        Assert.Equal(completed.EventId, index.Events[1].EventId);
        Assert.Equal(next.EventId, index.Events[2].EventId);
    }

    [Fact]
    public void 文字帧保留缺时间与有符号局部时间并独立表达退场()
    {
        var frame = Frame();
        var token = Assert.Single(frame.Presentations.TokensInBinding);
        Assert.Equal(ProgressSource.Missing, token.Performance.ProgressSource);
        Assert.Null(token.Performance.Progress); Assert.Null(token.Performance.ElapsedUs);
        var source = frame.Source;
        var oldFrame = LegacyFrame();
        var line = Assert.Single(oldFrame.Presentations.LinesInBinding);
        var replica = new TextFrame(source, oldFrame, [new(line, TextLinePhase.ExitReplica, 900, 100, 1)]);
        Assert.Equal(TextLinePhase.ExitReplica, Assert.Single(replica.LineLoads).Phase);
        Assert.Equal(line.BindingId, replica.LineLoads[0].Line.BindingId);
        Assert.Equal(1, replica.LineLoads[0].ExitProgress);
        Assert.Throws<ArgumentException>(() => new TextLineLoad(line, TextLinePhase.ExitReplica, 900, 3_000_001, 1));
        Assert.Throws<ArgumentException>(() => new TextLineLoad(line, TextLinePhase.ExitReplica, 900, 0, 0));
        var sample = new PerformanceSample(new(100, 200), 100, -1, 0, false, false, ProgressSource.Token);
        Assert.Equal(-1, sample.ElapsedUs);
    }

    [Fact]
    public void 输出按调用顺序记录且完成后快照不随句柄失效改变()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby;
        var output = new TextOutput(in frame, ["token.affine", "token.opacity", "style.fill", "style.stroke", "style.shadow", "style.glow"]);
        var a = Affine2.Translation(4, 5); var b = Affine2.ScaleAt(2, default);
        output.AppendTransform(target, a); output.AppendTransform(target, b);
        output.MultiplyOpacity(target, .5);
        output.SetFill(target, FillState.Sung, MaterialValue.Solid(new(1, 0, 0, 1)));
        output.SetStroke(target, null); output.SetShadow(target, new(new(0, 0, 0, 1))); output.SetGlow(target, null);
        var snapshot = output.Complete();
        Assert.Equal(7, snapshot.Writes.Length);
        var first = Assert.IsType<TextWrite.Transform>(snapshot.Writes[0]);
        var second = Assert.IsType<TextWrite.Transform>(snapshot.Writes[1]);
        Assert.Equal(new Point2(10, 12), second.LocalMatrix.Compose(first.LocalMatrix).Map(new(1, 1)));
        Assert.Equal(.5, Assert.IsType<TextWrite.Opacity>(snapshot.Writes[2]).Factor);
        Assert.Null(Assert.IsType<TextWrite.Stroke>(snapshot.Writes[4]).Value);
        output.Invalidate(); Assert.Equal(7, snapshot.Writes.Length);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.AppendTransform(target, Affine2.Identity)).Code);
        Assert.Equal("InvalidOutputStage", Assert.Throws<EffectOutputException>(() => output.Complete()).Code);
    }

    [Fact]
    public void 输出拒绝无能力越界非法浮点不可逆与不可独立连字()
    {
        var frame = Frame(); var target = frame.Geometry[0].WithRuby;
        var noCapability = new TextOutput(in frame, []);
        Assert.Equal("UndeclaredCapability", Assert.Throws<EffectOutputException>(() => noCapability.AppendTransform(target, Affine2.Identity)).Code);
        var output = new TextOutput(in frame, ["token.affine", "token.opacity", "line.affine"]);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.AppendTransform(new TokenFamilyTarget(new("other"), target.BindingId), Affine2.Identity)).Code);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(target, default)).Code);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(target, new(1e-100, 0, 0, 1e-100, 0, 0))).Code);
        Assert.Equal("InvalidTransform", Assert.Throws<EffectOutputException>(() => output.AppendTransform(target, new(1, 0, 0, 1, double.MaxValue, 0))).Code);
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<EffectOutputException>(() => output.MultiplyOpacity(target, double.NaN)).Code);
        Assert.Equal("InvalidStyleOpacity", Assert.Throws<EffectOutputException>(() => output.MultiplyOpacity(target, 1.1)).Code);
        var shared = Frame(false); var sharedOutput = new TextOutput(in shared, ["token.affine"]);
        Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedOutput.AppendTransform(shared.Geometry[0].WithRuby, Affine2.Translation(1, 0))).Code);
        var sharedStyles = new TextOutput(in shared, ["token.opacity", "style.fill", "style.stroke", "style.shadow", "style.glow"]);
        foreach (var sharedTarget in new TextTarget[] { shared.Geometry[0].WithRuby, shared.Geometry[0].BaseOnly })
        {
            Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedStyles.MultiplyOpacity(sharedTarget, .5)).Code);
            Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedStyles.SetFill(sharedTarget, FillState.Sung, MaterialValue.Solid(new(1, 0, 0, 1)))).Code);
            Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedStyles.SetStroke(sharedTarget, null)).Code);
            Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedStyles.SetShadow(sharedTarget, null)).Code);
            Assert.Equal("UnsupportedGlyphPartition", Assert.Throws<EffectOutputException>(() => sharedStyles.SetGlow(sharedTarget, null)).Code);
        }
        Assert.Empty(sharedStyles.Complete().Writes);
    }

    [Fact]
    public void 全局组几何不授写权且无后端支持的属性目标明确拒绝()
    {
        var frame = Frame(); var group = frame.GroupGeometry[0].Target;
        var output = new TextOutput(in frame, ["group.affine", "group.opacity", "style.fill", "line.affine"]);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.AppendTransform(group, Affine2.Identity)).Code);
        var scoped = new TextOutput(in frame, ["group.affine", "group.opacity", "style.fill", "line.affine"], [group]);
        scoped.AppendTransform(group, Affine2.Identity); scoped.MultiplyOpacity(group, .5);
        Assert.Equal("PropertyTargetUnsupported", Assert.Throws<EffectOutputException>(() => scoped.SetFill(group, FillState.Sung, MaterialValue.Solid(new(1, 1, 1, 1)))).Code);
        Assert.Equal("PropertyTargetUnsupported", Assert.Throws<EffectOutputException>(() => scoped.MultiplyOpacity(frame.LineLoads[0].Target, .5)).Code);
        Assert.Equal(2, scoped.Complete().Writes.Length);
    }

    [Fact]
    public void 基础字和字簇输出保留不同强类型目标且不能伪造单元序号()
    {
        var legacy = LegacyFrame(); var token = legacy.Geometry[0];
        var clusterGeometry = token.BaseGeometry with { DrawUnits = [new("unit-0")] };
        TextClusterTarget cluster = new(token.WithRuby.TokenId, token.WithRuby.BindingId, new("unit-0"), 0);
        token = token with { Clusters = [new(cluster, [token.WithRuby.TokenId], 0, 1, clusterGeometry)] };
        legacy = legacy with { Geometry = [token] };
        var source = Frame().Source; var frame = new TextFrame(source, legacy);
        var output = new TextOutput(in frame, ["token.affine", "cluster.affine"]);
        output.AppendTransform(token.BaseOnly, Affine2.Translation(2, 3));
        output.AppendTransform(cluster, Affine2.Translation(4, 5));
        var forged = new TextClusterTarget(cluster.TokenId, cluster.BindingId, cluster.DrawUnitId, 1);
        Assert.Equal("TargetOutsideBinding", Assert.Throws<EffectOutputException>(() => output.AppendTransform(forged, Affine2.Identity)).Code);
        var snapshot = output.Complete();
        Assert.IsType<TextTarget.Base>(snapshot.Writes[0].Target); Assert.IsType<TextTarget.Cluster>(snapshot.Writes[1].Target);
        Assert.Equal(cluster, Assert.Single(frame.Clusters).Target);
    }

    private sealed record TestPrepared(ImmutableArray<int> Ordinals) : PreparedEffect;

    private static TokenStarted Start(string token, string branch, string line, long startUs, int branchOrder)
        => new(new(new(token), new(branch), new(line), "repeat", new(startUs, startUs + 100), SourceTimingStatus.Confirmed, 0),
            new(branchOrder, 0, 0), ReplayReason.Initial);

    private static TextFrame Frame(bool independent = true)
    {
        var legacy = LegacyFrame(independent);
        var source = new DeclareContext(legacy.Project.Content, legacy.Project.Scene, ParameterValues.Empty,
            AudioFeatures.NotAnalyzed, ResourceCatalog.Empty, 42, TimelineIndex.Empty, new(1920, 1080), new("test"));
        return new(source, legacy);
    }

    private static FrameContext LegacyFrame(bool independent = true)
    {
        ContentCatalog content = new([], [], [], []); SceneCatalog scene = new(content, [], [], [], []);
        var family = new TokenFamilyTarget(new("token"), new("binding")); var basic = new BaseTokenTarget(family.TokenId, family.BindingId);
        var line = new FrameLine(new("line"), new("branch"), family.BindingId, new("node"), new("group"), new("program"), null, null, DisplayPhase.Performing, 1);
        var geometry = new GeometrySample { AdvanceBounds = new(0, 0, 10, 10), Baseline = new(0, 8), BaseWorldMatrix = Affine2.Identity,
            CanTransformIndependently = independent, DiagnosticCode = independent ? null : "UnsupportedGlyphPartition" };
        return new() { TimeUs = 1000, Project = new() { Content = content, Scene = scene, CanvasSize = new(1920, 1080) },
            Parameters = ParameterValues.Empty, Random = new(42, new("test")), Binding = new EffectBinding.Project(),
            Presentations = new() { LinesInBinding = [line], TokensInBinding = [new() { TokenId = family.TokenId, BranchId = line.BranchId,
                LineId = line.LineId, BindingId = family.BindingId, SourceText = "x", WithRuby = family, BaseOnly = basic,
                Performance = new(null, null, null, null, false, false, ProgressSource.Missing) }] },
            Geometry = [new() { BranchId = line.BranchId, LineId = line.LineId, WithRuby = family, BaseOnly = basic, BaseGeometry = geometry, FamilyGeometry = geometry }],
            LineGeometry = [new() { BranchId = line.BranchId, Target = line.Target, BaseGeometry = geometry, ProgressPath = new(0, 10, 8, 5) }],
            GroupGeometry = [new() { Target = new(new("group")), ReferenceBounds = new(0, 0, 100, 100), BaseWorldMatrix = Affine2.Identity, WorldPivot = default }] };
    }
}
