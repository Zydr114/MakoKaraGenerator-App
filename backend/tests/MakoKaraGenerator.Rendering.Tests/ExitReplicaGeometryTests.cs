using System.Text.Json;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

public sealed class ExitReplicaGeometryTests
{
    [Fact]
    public void 隐藏左极限冻结完整真实父链样式和字形且不进入布局或播放头()
    {
        var snapshot = Snapshot();
        using var native = new SkiaRenderPipeline();
        using var measured = new CountingGeometry();
        var timeline = TimelineProjection.Create(snapshot, Fx.ReplayReason.Export);
        var index = new PresentationIndex(timeline);
        var occurrence = Assert.Single(index.Query(1_250_000, 500_000));
        var before = native.GetSceneFrame(snapshot, occurrence.HideTimeUs - 1);
        var original = Assert.Single(before.Lines);
        var current = native.GetSceneFrame(snapshot, 1_250_000);
        var frozen = new FrozenExitGeometry(snapshot, measured).Extend(current, [occurrence]);
        var replica = Assert.Single(frozen.Lines.Where(static sample => sample.ExitReplica is not null));

        Assert.Equal([999_999L], measured.Calls);
        Assert.Equal(original.BindingId, replica.BindingId);
        Assert.Equal(original.SourceElementId, replica.SourceElementId);
        Assert.Equal(original.Frame.LineIndex, replica.Frame.LineIndex);
        Assert.Equal(original.Geometry, replica.Geometry);
        Assert.Equal(original.ParentMatrix, replica.ParentMatrix);
        Assert.Equal(original.ContainerMatrix, replica.ContainerMatrix);
        Assert.Equal(original.ParentReferenceWidth, replica.ParentReferenceWidth);
        Assert.Equal(original.ParentReferenceHeight, replica.ParentReferenceHeight);
        Assert.Equal(original.SlotId, replica.SlotId);
        Assert.Equal(1_250_000, replica.Frame.TimeUs);
        Assert.Equal(1, replica.Frame.LineProgress);
        Assert.NotEqual(original.ElementId, replica.ElementId);
        Assert.Equal(current.Lines.ToArray(), frozen.Lines.Where(static sample => sample.ExitReplica is null).ToArray());
        Assert.Same(current.Definition, frozen.Definition);
        Assert.Equal(current.LeadIns, frozen.LeadIns);
        Assert.Equal(current.TextMarks, frozen.TextMarks);
        Assert.Equal(current.Elements.ToArray(), frozen.Elements.Where(static node => node.SourceNodeId is null).ToArray());
        Assert.Equal(PlaybackProjection.GetSources(snapshot, current).ToArray(), PlaybackProjection.GetSources(snapshot, frozen).ToArray());
        Assert.Equal(Json(timeline.Events), Json(TimelineProjection.Create(snapshot, Fx.ReplayReason.Export).Events));

        var originalPath = PresetSceneStyleProjection.NodePath(snapshot, original.ElementId, before);
        Assert.Equal(originalPath.ToArray(), replica.ExitReplica!.SourceNodePath.ToArray());
        var nodes = frozen.Elements.ToDictionary(static node => node.Id, StringComparer.Ordinal);
        List<string> sourcePath = [];
        string? id = replica.ElementId;
        while (id is not null)
        {
            var clone = nodes[id];
            Assert.NotNull(clone.SourceNodeId);
            var source = before.Elements.Single(node => node.Id == clone.SourceNodeId);
            Assert.Equal(source.WorldMatrix, clone.WorldMatrix);
            Assert.Equal(source.Pivot, clone.Pivot);
            sourcePath.Add(clone.SourceNodeId!); id = clone.ParentId;
        }
        sourcePath.Reverse();
        Assert.Equal(originalPath, sourcePath);
        var ruby = Assert.Single(frozen.Elements.Where(node => node.SourceNodeId is not null && node.SourceBinding.RubyId is not null));
        Assert.Equal(replica.ElementId, ruby.ParentId);
        var target = new Fx.TokenFamilyTarget(new("token-0"), occurrence.Binding.BindingId);
        Assert.Equal(Json(before.TokenGeometries.Single(token => token.WithRuby == target)),
            Json(frozen.TokenGeometries.Single(token => token.WithRuby == target)));
        var baseline = StaticStyleProjection.CreateBaseline(snapshot, scene: frozen);
        var oldStyle = PresetSceneStyleProjection.LyricsForBinding(snapshot, original, "token-0", before).Style;
        Assert.Equal(StaticStyleProjection.View(oldStyle), baseline.Get(target));
        Assert.Equal(0.625, baseline.Get(target).Opacity);
        Assert.Equal(FrameProjection.Groups(current).ToArray(), FrameProjection.Groups(frozen).ToArray());
    }

    [Fact]
    public void 冻结路径支持当前组变换及透明度各传播一次且别名不取得作者写权()
    {
        var snapshot = Snapshot(); using var native = new SkiaRenderPipeline();
        var index = new PresentationIndex(TimelineProjection.Create(snapshot, Fx.ReplayReason.Export));
        var occurrence = Assert.Single(index.Query(1_250_000, 500_000));
        var scene = new FrozenExitGeometry(snapshot, native).Extend(native.GetSceneFrame(snapshot, 1_250_000), [occurrence]);
        var replica = scene.Lines.Single(static line => line.ExitReplica is not null);
        var originalGroups = scene.Elements.Where(static node => node.Kind == SceneElementKind.Group && node.SourceNodeId is null).ToArray();
        string root = snapshot.LyricPresentation.FindProgram("primary")!.RootContainerId;
        var rootTarget = new Fx.GroupTarget(new(root));
        var local = Fx.Affine2.Translation(11, -7);
        var transformed = SceneAffineProjection.Apply(scene, [], [new(rootTarget, local)]);
        var basis = scene.GroupGeometries.Single(group => group.Target == rootTarget).BaseWorldMatrix;
        Assert.True(basis.TryInverse(out var inverse));
        var delta = basis.Compose(local).Compose(inverse);
        foreach (var sample in scene.Lines)
        {
            var target = new Fx.LineTarget(new(sample.Frame.Line!.Id), new(sample.BindingId!));
            var before = scene.LineGeometries.Single(line => line.Target == target).BaseGeometry.BaseWorldMatrix;
            var after = transformed.LineGeometries.Single(line => line.Target == target).BaseGeometry.BaseWorldMatrix;
            AssertMatrix(delta.Compose(before), after);
        }
        var scope = new FrameBindingScope(scene);
        var lines = FrameProjection.Lines(scene, SceneProjection.Create(snapshot, ContentProjection.Create(snapshot)));
        var exit = lines.Single(line => line.BindingId == occurrence.Binding.BindingId);
        Assert.True(scope.ContainsLine(new Fx.EffectBinding.Node(new(root), true), exit));
        Assert.True(scope.ContainsLine(new Fx.EffectBinding.Node(new("bridge"), true), exit));
        Assert.False(scope.ContainsLine(new Fx.EffectBinding.Node(new(root), false), exit));
        Assert.All(originalGroups, group => Assert.True(scope.ContainsGroup(new Fx.EffectBinding.Project(), new(new(group.Id)))));
        var alias = scene.GroupGeometries.Single(group => scene.Elements.Single(node => node.Id == group.Target.NodeId.Value).SourceNodeId == root).Target;
        Assert.False(scope.ContainsGroup(new Fx.EffectBinding.Project(), alias));
        Assert.Equal("InvalidPreparedGroupTarget", Assert.Throws<PreparedFrameException>(() => SceneAffineProjection.Apply(scene, [], [new(alias, local)])).Code);
        var prepared = new PreparedRenderFrame(snapshot, scene.TimeUs, Fx.OutputMode.Transparent,
            RenderTarget.FromComposition(snapshot.Composition), scene, []);
        var opacity = new PreparedGroupOpacity(new("group"), "artifact", new(rootTarget, .5));
        var final = prepared.WithGroupOpacities([opacity]);
        Assert.Equal(2, final.GroupOpacities.Length);
        Assert.Contains(final.GroupOpacities, item => item.Opacity.Target == alias && item.Opacity.Factor == .5
            && item.InstanceId == opacity.InstanceId && item.ArtifactHash == opacity.ArtifactHash);
        Assert.Equal("InvalidPreparedGroupOpacity", Assert.Throws<PreparedFrameException>(() => prepared.WithGroupOpacities([opacity with
            { Opacity = new(alias, .5) }])).Code);
        Assert.Equal(replica.Geometry, scene.Lines.Single(line => line.BindingId == replica.BindingId).Geometry);

        var nested = prepared.WithGroupOpacities([opacity, new PreparedGroupOpacity(new("child"), "artifact-child", new(new(new("bridge")), .5))]);
        var request = RenderRequest.At(snapshot, scene.TimeUs) with { TransparentBackground = true, PreparedFrame = prepared };
        using var originalPixels = native.Render(request);
        using var halfPixels = native.Render(request with { PreparedFrame = final });
        using var quarterPixels = native.Render(request with { PreparedFrame = nested });
        Assert.Contains(originalPixels.Pixels.ToArray(), static value => value > 0);
        for (int i = 0; i < originalPixels.Pixels.Length; i++)
        {
            Assert.InRange(Math.Abs(halfPixels.Pixels[i] - originalPixels.Pixels[i] * .5), 0, 2);
            Assert.InRange(Math.Abs(quarterPixels.Pixels[i] - originalPixels.Pixels[i] * .25), 0, 3);
        }
    }

    [Fact]
    public void 索引遵循半开边界固定装载顺序并为每个实例独立计算终点()
    {
        var snapshot = Snapshot(); using var native = new SkiaRenderPipeline();
        var timeline = TimelineProjection.Create(snapshot, Fx.ReplayReason.Export);
        var index = new PresentationIndex(timeline);
        Assert.Empty(index.Query(999_999, 500_000));
        var occurrence = Assert.Single(index.Query(1_000_000, 500_000));
        Assert.Equal(0, occurrence.LoadOrdinal);
        Assert.Single(index.Query(1_499_999, 500_000));
        Assert.Empty(index.Query(1_500_000, 500_000));
        Assert.Empty(index.Query(1_000_000, 0));
        Assert.Empty(index.Query(1_000_000, 500_000, static _ => false));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Query(1_000_000, 3_000_001));
        var scene = new FrozenExitGeometry(snapshot, native).Extend(native.GetSceneFrame(snapshot, 1_250_000), [occurrence]);
        var lines = FrameProjection.Lines(scene, timeline.Scene);
        var shortLoad = PresentationIndex.LineLoads(scene, lines, 100_000).Single(load => load.Phase == TextLinePhase.ExitReplica);
        var longLoad = PresentationIndex.LineLoads(scene, lines, 500_000).Single(load => load.Phase == TextLinePhase.ExitReplica);
        var zeroLoad = PresentationIndex.LineLoads(scene, lines, 0).Single(load => load.Phase == TextLinePhase.ExitReplica);
        Assert.Equal(1, shortLoad.ExitProgress); Assert.Equal(.5, longLoad.ExitProgress); Assert.Equal(1, zeroLoad.ExitProgress);
        Assert.Equal(100_000, shortLoad.ExitDurationUs); Assert.Equal(500_000, longLoad.ExitDurationUs); Assert.Equal(0, zeroLoad.ExitDurationUs);
        Assert.All(PresentationIndex.LineLoads(scene, lines, 500_000).Where(load => load.Phase == TextLinePhase.Active),
            load => { Assert.Null(load.HideTimeUs); Assert.Equal(0, load.ExitProgress); });
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PresentationIndex(timeline, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => index.Query(1_000_000, 500_000, token: cancellation.Token));
    }

    [Fact]
    public void 有界缓存淘汰后按隐藏左极限重算且取消重复绑定和预算明确拒绝()
    {
        var snapshot = Snapshot(); using var native = new SkiaRenderPipeline(); using var measured = new CountingGeometry();
        var index = new PresentationIndex(TimelineProjection.Create(snapshot, Fx.ReplayReason.Export));
        var cache = new FrozenExitGeometry(snapshot, measured, maximumEntries: 1);
        SceneFrame Extend(long time) => cache.Extend(native.GetSceneFrame(snapshot, time), index.Query(time, 500_000));
        var first = Extend(1_250_000); var repeat = Extend(1_250_000);
        Assert.Equal([999_999L], measured.Calls);
        _ = Extend(2_250_000);
        var afterEviction = Extend(1_250_000);
        Assert.Equal([999_999L, 1_999_999L, 999_999L], measured.Calls);
        Assert.Equal(Canonical(first), Canonical(repeat)); Assert.Equal(Canonical(first), Canonical(afterEviction));
        var current = native.GetSceneFrame(snapshot, 1_250_000);
        var occurrence = Assert.Single(index.Query(current.TimeUs, 500_000));
        Assert.Equal("InvalidExitReplica", Assert.Throws<PreparedFrameException>(() => cache.Extend(current, [occurrence, occurrence])).Code);
        Assert.Equal("ExitReplicaBudgetExceeded", Assert.Throws<PreparedFrameException>(() => cache.Extend(current,
            [.. Enumerable.Repeat(occurrence, 129)])).Code);
        Assert.Equal("ExitReplicaBudgetExceeded", Assert.Throws<PreparedFrameException>(() => cache.Extend(first, [occurrence])).Code);
        Assert.Equal("ExitReplicaGeometryUnavailable", Assert.Throws<PreparedFrameException>(() => cache.Extend(current,
            [occurrence with { Binding = occurrence.Binding with { BindingId = new("missing") } }])).Code);
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => cache.Extend(current, [occurrence], cancellation.Token));
        Assert.Equal(Canonical(first), Canonical(Extend(1_250_000)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FrozenExitGeometry(snapshot, native, 129));
    }

    private static ProjectSnapshot Snapshot()
    {
        var rows = Enumerable.Range(0, 4).Select(index => LyricLine.Create("line-" + index, "歌", new(index * 1_000_000L, (index + 1) * 1_000_000L),
            TimingStatus.Edited, [LyricToken.Create("token-" + index, "歌", new(index * 1_000_000L, (index + 1) * 1_000_000L), TimingStatus.Edited)])
            with { RubyAnnotations = [RubyAnnotation.Create("ruby-" + index, "token-" + index, "うた")] });
        var project = Project.Create("冻结副本", Composition.Create(640, 360, FrameRate.Fps30, outputDurationUs: 5_000_000), LyricsDocument.Create(rows),
            lyricStyle: LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 40, Stroke = null, Shadow = null });
        project = PresentationEditor.SetBranch(project, new() { BranchId = "primary", SlotSequenceProfile = null,
            Placement = ScenePlacement.Create(new(.52, .48), rotationDegrees: 17, scale: .8) });
        var tree = project.LyricPresentation; var program = tree.FindProgram("primary")!;
        var bridge = new SceneContainerDefinition { Id = "bridge", ParentId = program.RootContainerId, ReferenceRect = new(.75, .8),
            Placement = ScenePlacement.Create(new(.4, .45), rotationDegrees: -11, scale: 1.1) };
        tree = tree with { Nodes = [.. tree.Nodes.Select(node => node.Id == program.DefaultContainerId ? node with { ParentId = bridge.Id } : node), bridge] };
        project = project with { LyricPresentation = tree };
        var style = project.LyricStyle with { SungColor = new(20, 180, 220, 255), UnsungColor = new(240, 100, 30, 255), Opacity = .625f };
        var preset = StyleTemplate.Create("frozen-source", "冻结源", StyleTemplateKind.Composite, style,
            [LyricStyleProperty.SungColor, LyricStyleProperty.UnsungColor, LyricStyleProperty.Opacity]);
        Assert.True(PresetApplicationsEditor.TryApply(project, preset,
            [new(ProjectEffectBindingKind.Node, id: "bridge", includeDescendants: true)], null, out project, out var reason), reason);
        return ProjectSnapshot.FromProject(project);
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value);
    private static string Canonical(SceneFrame frame) => Json(new
    {
        frame.TimeUs, frame.Definition, frame.Elements, frame.TokenGeometries, frame.LineGeometries, frame.GroupGeometries,
        frame.LeadIns, frame.TextMarks, frame.MarkGeometries,
        Lines = frame.Lines.Select(line => new { Sample = line with { ExitReplica = null }, Exit = line.ExitReplica is { } exit ? new
        {
            exit.HideTimeUs, exit.LoadOrdinal, exit.SourceNodePath,
            LineStyle = new { exit.LineStyle.Style, Sources = exit.LineStyle.Sources.OrderBy(static pair => pair.Key) },
            TokenStyles = exit.TokenStyles.OrderBy(static pair => pair.Key.Value, StringComparer.Ordinal).Select(pair => new
                { TokenId = pair.Key.Value, pair.Value.Style, Sources = pair.Value.Sources.OrderBy(static source => source.Key) }),
        } : null }),
    });
    private static void AssertMatrix(Fx.Affine2 expected, Fx.Affine2 actual)
    {
        Assert.Equal(expected.A, actual.A, 8); Assert.Equal(expected.B, actual.B, 8); Assert.Equal(expected.C, actual.C, 8);
        Assert.Equal(expected.D, actual.D, 8); Assert.Equal(expected.Tx, actual.Tx, 8); Assert.Equal(expected.Ty, actual.Ty, 8);
    }
    private sealed class CountingGeometry : ILineGeometryProvider, IDisposable
    {
        private readonly SkiaRenderPipeline _native = new();
        internal List<long> Calls { get; } = [];
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        { Calls.Add(timeUs); return _native.GetSceneFrame(snapshot, timeUs, editingLineId); }
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => _native.GetLineGeometry(snapshot, lineId);
        public void Dispose() => _native.Dispose();
    }
}
