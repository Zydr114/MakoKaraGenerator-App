using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.Scripting.Projection;
using MakoKaraGenerator.Scripting.V2;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Scripting.Tests;

public sealed class PureSourceIndexesTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void 真实揭示隐藏准备和原布局退出窗逐边界对照生产采样(bool slots, bool crossClip)
    {
        var project = LeadInFixture.Create(slots, crossClip);
        if (slots)
        {
            var primary = project.LyricPresentation.Resolve("primary");
            project = PresentationEditor.SetBranch(project, primary with
            { SlotSequenceProfile = primary.SlotSequenceProfile! with { EnterDurationUs = 1_000_000, ExitDurationUs = 700_000 } });
            project = PresentationEditor.SetBranch(project, project.LyricPresentation.Resolve("translation") with
            { AnimationPolicy = LyricBranchAnimationPolicy.FollowPrimary });
        }
        var snapshot = ProjectSnapshot.FromProject(project); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var geometry = new Geometry(); var indexes = Create(snapshot, timeline, geometry);
        var loads = indexes.Presentations.GetRange(new(0, 60_000_000), new SourceFilter.Project());
        Assert.Equal(timeline.Events.OfType<BindingEntered>().Select(item => item.Binding.BindingId), loads.OrderBy(item => item.LoadOrdinal).Select(item => item.Binding.BindingId));
        HashSet<long> queries = [0, 60_000_000];
        foreach (var load in loads)
        {
            Assert.Same(load, indexes.Presentations.Get(load.Binding.BindingId));
            Add(load.RevealTimeUs); if (load.HideTimeUs is { } hide) Add(hide);
            if (load.PreparationRange is { } preparation) { Add(preparation.StartUs); Add(preparation.EndUs); }
            if (load.LayoutExitRange is { } exit) { Add(exit.StartUs); Add(exit.EndUs); }
        }
        foreach (long time in queries.Order())
        {
            var frame = LyricSceneSampler.SampleFrame(snapshot, time);
            var expected = FrameProjection.Lines(frame, timeline.Scene).ToDictionary(static line => line.BindingId);
            var actual = indexes.Presentations.GetAt(time, new SourceFilter.Project());
            Assert.Equal(expected.Keys.OrderBy(static id => id.Value, StringComparer.Ordinal), actual.Select(static item => item.Load.Binding.BindingId).OrderBy(static id => id.Value, StringComparer.Ordinal));
            foreach (var sample in actual)
            {
                Assert.Equal(expected[sample.Load.Binding.BindingId].DisplayPhase, sample.DisplayPhase);
                var source = frame.Lines.Single(line => line.BindingId == sample.Load.Binding.BindingId.Value);
                Assert.Equal(source.ExitProgress, sample.LayoutExitProgress, precision: 12);
                Assert.True(sample.Load.Contains(time));
            }
        }
        Assert.Empty(geometry.Queries); // Fixed presentation queries do not sample geometry or replay author code.
        Assert.Equal(12, indexes.Timeline.Count(new(TimelineEventKind.TokenStarted)));
        if (slots) Assert.Contains(loads, static load => load.LayoutExitRange is not null);
        void Add(long boundary)
        { queries.Add(boundary); if (boundary > 0) queries.Add(boundary - 1); if (boundary < long.MaxValue) queries.Add(boundary + 1); }
    }

    [Fact]
    public void 源订阅固定且Project查询不能扩大行元素或分支范围()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var geometry = new Geometry();
        var narrow = PureSourceIndexes.Create(snapshot, timeline, geometry, new EffectBinding.Tokens([new("a")]), new SourceSubscription.BoundSources());
        Assert.Equal(["a"], narrow.Timeline.Events.OfType<TokenStarted>().Select(static item => item.TokenId.Value));
        Assert.All(narrow.Presentations.GetRange(new(0, 9000), new SourceFilter.Project()), static load => Assert.Equal("first", load.Binding.LineId.Value));
        Assert.Empty(narrow.Presentations.GetAt(6500, new SourceFilter.Project()));
        Assert.Equal(["a"], narrow.Layout.GetBaseAt(1500, new SourceFilter.Project()).Tokens.Select(static item => item.WithRuby.TokenId.Value));
        Assert.Empty(narrow.Cursors.GetBaseAt(2500, new SourceFilter.Project(), CursorAnchorMode.Baseline));
        Assert.Empty(narrow.Layout.GetBaseAt(1500, new SourceFilter.Tokens([new("b")])).Tokens);
        var wide = PureSourceIndexes.Create(snapshot, timeline, geometry, new EffectBinding.Tokens([new("a")]), new SourceSubscription.Project());
        Assert.Equal(3, wide.Timeline.Count(new(TimelineEventKind.TokenStarted)));
        Assert.Equal("fallback", Assert.Single(wide.Presentations.GetAt(6500, new SourceFilter.Project())).Load.Binding.LineId.Value);
        var branches = ProjectSnapshot.FromProject(LeadInFixture.Create(true, true)); var branchTimeline = TimelineProjection.Create(branches, ReplayReason.Initial);
        var onlyTranslation = PureSourceIndexes.Create(branches, branchTimeline, geometry, new EffectBinding.Project(), new SourceSubscription.Branch(new("translation")));
        Assert.All(onlyTranslation.Presentations.GetRange(new(0, 60_000_000), new SourceFilter.Project()), static load => Assert.Equal("translation", load.Binding.BranchId.Value));
        Assert.Empty(onlyTranslation.Presentations.GetAt(12_000_000, new SourceFilter.Branch(new("primary"))));
        Assert.Empty(onlyTranslation.Timeline.Events.OfType<TokenStarted>());
    }

    [Fact]
    public void 播放头区段按真实轨道身份半开且保留缺时间间隙和整行回退()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var geometry = new Geometry(); var indexes = Create(snapshot, timeline, geometry);
        var first = timeline.Events.OfType<BindingEntered>().First(); var track = StableIdentities.CursorTrack(first.Binding.BranchId, first.Binding.ProgramId);
        var segments = indexes.Cursors.GetSegments(track, new(0, 9000));
        Assert.Equal(["a", "b", "c", null], segments.Select(static segment => segment.Identity.TokenId?.Value));
        Assert.Equal([new TimeRange(1000, 2000), new(2000, 3000), new(4000, 5000), new(6000, 7000)], segments.Select(static segment => segment.ActiveRange));
        Assert.Equal(segments.Select(static item => item.ActiveRange), segments.Select(static item => item.Identity.TimeRange));
        Assert.Empty(geometry.Queries);
        foreach (long time in new long[] { 999, 1000, 1999, 2000, 2999, 3000, 3999, 4000, 4999, 5000, 6000, 6999, 7000 })
        {
            var source = PlaybackProjection.GetSources(snapshot, LyricSceneSampler.SampleFrame(snapshot, time));
            var current = indexes.Cursors.GetBaseAt(time, new SourceFilter.Project(), CursorAnchorMode.Baseline);
            Assert.Equal(source.Select(static item => item.Identity), current.Select(static item => item.Identity));
            Assert.All(current, cursor => Assert.Equal((double)(time - cursor.Identity.TimeRange.StartUs) / cursor.Identity.TimeRange.DurationUs, cursor.Progress));
        }
        Assert.Equal("b", Assert.Single(indexes.Cursors.GetSegments(track, new(2000, 2001))).Identity.TokenId?.Value);
        Assert.Empty(indexes.Cursors.GetSegments(new("absent-track"), new(0, 9000)));
        Assert.Empty(indexes.Cursors.GetBaseAt(long.MaxValue, new SourceFilter.Project(), CursorAnchorMode.Baseline));
    }

    [Fact]
    public void 槽位提前隐藏剪裁区段但不重写原始演唱时间和进度()
    {
        var original = Project.Create("clip", lyrics: LyricsDocument.Create([
            CursorTransitionProjectionTests.Row("from", 0, 500), CursorTransitionProjectionTests.Row("next", 300, 800)]));
        var profile = SlotSequenceAnimationProfile.Default with { EnterDurationUs = 0, ExitDurationUs = 0, MaxVisibleLines = 1,
            RevealMode = SlotRevealMode.LeadBeforeNextStart, ConflictPolicy = SlotConflictPolicy.ReplacePrevious,
            Slots = [SlotSequenceSlot.Create("only", ScenePlacement.Default)] };
        var snapshot = ProjectSnapshot.FromProject(PresentationEditor.SetBranch(original, new() { BranchId = "primary", SlotSequenceProfile = profile }));
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial); var indexes = Create(snapshot, timeline, new Geometry());
        var first = timeline.Events.OfType<BindingEntered>().First(); var track = StableIdentities.CursorTrack(first.Binding.BranchId, first.Binding.ProgramId);
        var from = indexes.Cursors.GetSegments(track, new(0, 1000)).Single(static item => item.Identity.LineId.Value == "from");
        Assert.Equal(new TimeRange(0, 500), from.Identity.TimeRange); Assert.Equal(new TimeRange(0, 300), from.ActiveRange);
        var before = Assert.Single(indexes.Cursors.GetBaseAt(299, new SourceFilter.Project(), CursorAnchorMode.Baseline));
        Assert.Equal(299d / 500, before.Progress);
        Assert.Equal("next", Assert.Single(indexes.Cursors.GetBaseAt(300, new SourceFilter.Project(), CursorAnchorMode.Baseline)).LineId.Value);
        Assert.Contains(indexes.Timeline.Events.OfType<TokenCompleted>(), static item => item.TokenId.Value == "from-token" && item.TimeUs == 500);
    }

    [Fact]
    public void 任意未来乱序查询共享同刻布局且冷缓存和淘汰重算结果一致()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var geometry = new Geometry(); var indexes = Create(snapshot, timeline, geometry);
        var future = indexes.Layout.GetBaseAt(1_000_000, new SourceFilter.Project());
        Assert.Empty(future.Lines); Assert.Equal([1_000_000L], geometry.Queries);
        var baseline = indexes.Layout.GetBaseAt(1500, new SourceFilter.Project());
        var cursor = Assert.Single(indexes.Cursors.GetBaseAt(1500, new SourceFilter.Project(), CursorAnchorMode.Baseline));
        Assert.Equal(new Point2(50, 20), cursor.Position); Assert.Equal(2, geometry.Queries.Count);
        var center = Assert.Single(indexes.Cursors.GetBaseAt(1500, new SourceFilter.Project(), CursorAnchorMode.AdvanceCenter));
        Assert.Equal(new Point2(50, 10), center.Position); Assert.Equal(2, geometry.Queries.Count);
        var cold = Create(snapshot, timeline, new Geometry());
        // Select sequence equality explicitly: ImmutableArray.Equals compares the backing array identity.
        Assert.Equal<FrameLine>(baseline.Lines, cold.Layout.GetBaseAt(1500, new SourceFilter.Project()).Lines);
        Assert.Equal(cursor, Assert.Single(cold.Cursors.GetBaseAt(1500, new SourceFilter.Project(), CursorAnchorMode.Baseline)));
        foreach (long time in Enumerable.Range(1, 9).Select(static value => 10_000L + value)) indexes.Layout.GetBaseAt(time, new SourceFilter.Project());
        var repeated = indexes.Layout.GetBaseAt(1500, new SourceFilter.Project());
        Assert.Equal<FrameLine>(baseline.Lines, repeated.Lines);
        Assert.Equal(cursor, Assert.Single(indexes.Cursors.GetBaseAt(1500, new SourceFilter.Project(), CursorAnchorMode.Baseline)));
        Assert.Equal(2, geometry.Queries.Count(static time => time == 1500));
        Assert.All(geometry.Inputs, input => Assert.Same(snapshot, input));
        Assert.All(geometry.EditingOverrides, Assert.Null);
    }

    [Fact]
    public void 几何缺失和错误采样时刻具名拒绝且取消保持原异常类型()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var missing = Create(snapshot, timeline, new Geometry(omitPath: true));
        Assert.Equal("BaseCursorGeometryUnavailable", Assert.Throws<EffectOutputException>(() => missing.Cursors.GetBaseAt(1500, new SourceFilter.Project(), CursorAnchorMode.Baseline)).Code);
        var replica = Create(snapshot, timeline, new Geometry(replica: true));
        Assert.Equal("InvalidSourceLayout", Assert.Throws<EffectOutputException>(() => replica.Layout.GetBaseAt(1500, new SourceFilter.Project())).Code);
        var wrongTime = Create(snapshot, timeline, new Geometry(wrongTime: true));
        Assert.Equal("InvalidSourceLayout", Assert.Throws<EffectOutputException>(() => wrongTime.Layout.GetBaseAt(1500, new SourceFilter.Project())).Code);
        using var cancel = new CancellationTokenSource();
        var indexes = PureSourceIndexes.Create(snapshot, timeline, new Geometry(), new EffectBinding.Project(), new SourceSubscription.Project(), cancel.Token);
        cancel.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => indexes.Presentations.GetAt(1500, new SourceFilter.Project()));
        Assert.ThrowsAny<OperationCanceledException>(() => indexes.Layout.GetBaseAt(1500, new SourceFilter.Project()));
        Assert.ThrowsAny<OperationCanceledException>(() => PureSourceIndexes.Create(snapshot, timeline, new Geometry(), new EffectBinding.Project(), new SourceSubscription.Project(), cancel.Token));
    }

    [Fact]
    public void 不明隐藏保留null且重用装载不重复源起唱()
    {
        var snapshot = TimelineProjectionTests.Source(); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var indexes = Create(snapshot, timeline, new Geometry());
        var entered = timeline.Events.OfType<BindingEntered>().Where(item => !timeline.Events.OfType<BindingLeft>().Any(left => left.Binding.BindingId == item.Binding.BindingId)).ToArray();
        var realBinding = timeline.Events.OfType<BindingEntered>().First();
        var openLoad = new SourcePresentationLoad(realBinding.Binding, realBinding.TimeUs, null, 0);
        Assert.Null(openLoad.HideTimeUs); Assert.True(openLoad.Contains(long.MaxValue));
        foreach (var item in entered)
        {
            var load = indexes.Presentations.Get(item.Binding.BindingId)!;
            Assert.Null(load.HideTimeUs); Assert.True(load.Contains(long.MaxValue));
            Assert.Contains(indexes.Presentations.GetAt(long.MaxValue, new SourceFilter.Project()), sample => sample.Load.Binding.BindingId == item.Binding.BindingId);
        }
        Assert.Equal(timeline.Events.OfType<TokenStarted>().Select(static item => item.OccurrenceId), indexes.Timeline.Events.OfType<TokenStarted>().Select(static item => item.OccurrenceId));
        Assert.Null(indexes.Presentations.Get(new("absent-binding")));
    }

    [Fact]
    public void 同源遮挡后再次揭示拥有独立Binding且不增加起唱()
    {
        var project = Project.Create("reentry", lyrics: LyricsDocument.Create([
            CursorTransitionProjectionTests.Row("outer", 0, 1000), CursorTransitionProjectionTests.Row("inner", 500, 700)]));
        var snapshot = WithV2(ProjectSnapshot.FromProject(project)); var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial);
        var indexes = Create(snapshot, timeline, new Geometry());
        var loads = indexes.Presentations.GetRange(new(0, 2000), new SourceFilter.Project());
        Assert.Equal(["outer", "inner", "outer"], loads.Select(static load => load.Binding.LineId.Value));
        Assert.Equal([0, 1, 2], loads.Select(static load => load.LoadOrdinal));
        Assert.NotEqual(loads[0].Binding.BindingId, loads[2].Binding.BindingId);
        Assert.Equal(500, loads[0].HideTimeUs); Assert.Equal(700, loads[2].RevealTimeUs); Assert.Equal(1000, loads[2].HideTimeUs);
        Assert.Same(loads[2], indexes.Presentations.Get(loads[2].Binding.BindingId));
        Assert.Same(loads[2], Assert.Single(indexes.Presentations.GetAt(700, new SourceFilter.Project())).Load);
        Assert.Equal(2, indexes.Timeline.Count(new(TimelineEventKind.TokenStarted)));
        var cursor = Assert.Single(indexes.Cursors.GetBaseAt(750, new SourceFilter.Project(), CursorAnchorMode.Baseline));
        Assert.Equal("outer", cursor.LineId.Value); Assert.Equal(.75, cursor.Progress);
        var segments = indexes.Cursors.GetSegments(cursor.TrackId, new(0, 2000));
        Assert.Equal([new TimeRange(0, 500), new(500, 700), new(700, 1000)], segments.Select(static item => item.ActiveRange));
    }

    [Fact]
    public void long最大端点仍是有限播放区段的半开终点()
    {
        var snapshot = ProjectSnapshot.FromProject(Project.Create("max", lyrics: LyricsDocument.Create([
            CursorTransitionProjectionTests.Row("finite", 0, long.MaxValue)])));
        var timeline = TimelineProjection.Create(snapshot, ReplayReason.Initial); var indexes = Create(snapshot, timeline, new Geometry());
        Assert.Empty(indexes.Cursors.GetBaseAt(long.MaxValue, new SourceFilter.Project(), CursorAnchorMode.Baseline));
        Assert.Equal("finite", Assert.Single(indexes.Cursors.GetBaseAt(long.MaxValue - 1, new SourceFilter.Project(), CursorAnchorMode.Baseline)).LineId.Value);
    }

    private static ProjectSnapshot WithV2(ProjectSnapshot snapshot)
    {
        var files = EffectPackageFixture.Files();
        files["src/PackEffect.cs"] = Encoding.UTF8.GetBytes("""
            using MakoKaraGenerator.Scripting.V2;
            public sealed class PackEffect : IPureKaraokeEffect
            {
                public PreparedEffect Declare(DeclareContext source) => PreparedEffect.Empty;
                public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output) { }
            }
            """);
        var manifest = EffectPackageFixture.Manifest(files);
        manifest["packageVersion"] = 3; manifest["sdkVersion"] = "2.0.0"; manifest["shaderProfile"] = EffectPackageProfiles.V2.ShaderProfile;
        manifest["requires"] = new JsonArray("token.affine"); manifest["emitters"] = new JsonArray();
        manifest["lineExit"] = new JsonObject { ["durationUs"] = 0 };
        manifest["exports"]![0]!["bindingKinds"] = new JsonArray("project");
        var package = EffectPackageContents.CreateSupported(EffectPackageFixture.Bytes(manifest), EffectPackageFixture.Inputs(files));
        var definition = EffectPackageDefinition.Read(package);
        return snapshot with { Effects = new([package], [new() { Id = "index-v2", Name = "index-v2",
            PackageId = package.PackageId, Revision = package.Revision, ExportId = definition.Controllers.Single().Id }]) };
    }

    private static SourceIndexes Create(ProjectSnapshot snapshot, TimelineProjection timeline, Geometry geometry)
        => PureSourceIndexes.Create(snapshot, timeline, geometry, new EffectBinding.Project(), new SourceSubscription.Project());

    // These unit tests deliberately provide neutral geometry over the actual production scene sampler.
    // They verify indexing/identity/cache semantics, not font shaping or renderer pixels.
    private sealed class Geometry(bool omitPath = false, bool wrongTime = false, bool replica = false) : ILineGeometryProvider
    {
        internal List<long> Queries { get; } = [];
        internal List<ProjectSnapshot> Inputs { get; } = [];
        internal List<string?> EditingOverrides { get; } = [];
        public LineRenderGeometry? GetLineGeometry(ProjectSnapshot snapshot, string lineId) => null;
        public SceneFrame GetSceneFrame(ProjectSnapshot snapshot, long timeUs, string? editingLineId = null)
        {
            Queries.Add(timeUs); Inputs.Add(snapshot); EditingOverrides.Add(editingLineId);
            var scene = LyricSceneSampler.SampleFrame(snapshot, timeUs); var path = new TextProgressPath(0, 100, 20, 10);
            GeometrySample shape = new() { AdvanceBounds = new(0, 0, 100, 20), Baseline = new(0, 20), BaseWorldMatrix = Affine2.Identity };
            var lines = scene.Lines.Select(line => new LineGeometrySample
            { BranchId = new(line.Branch.Id), Target = new(new(line.Frame.Line!.Id), new(line.BindingId!)), BaseGeometry = shape, ProgressPath = path }).ToImmutableArray();
            var tokens = FrameProjection.Tokens(scene).Select(token => new TokenGeometrySample
            { BranchId = token.BranchId, LineId = token.LineId, WithRuby = token.WithRuby, BaseOnly = token.BaseOnly,
                BaseGeometry = shape, FamilyGeometry = shape, ProgressPath = omitPath ? null : path }).ToImmutableArray();
            if (replica)
            {
                var line = scene.Lines[0];
                var metadata = new ExitReplicaMetadata(timeUs, 0, [], PresetSceneStyleProjection.LyricsForBinding(snapshot, line, null, scene),
                    ImmutableDictionary<TokenId, PresetStyleResolution>.Empty);
                scene = scene with { Lines = scene.Lines.SetItem(0, line with { ExitReplica = metadata }) };
            }
            return scene with { TimeUs = wrongTime ? timeUs + 1 : timeUs, LineGeometries = lines, TokenGeometries = tokens };
        }
    }
}
