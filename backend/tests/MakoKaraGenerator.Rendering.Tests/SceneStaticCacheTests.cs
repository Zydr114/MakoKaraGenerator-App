using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering.Skia;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>逐帧共用的静态场景定义、样式解析器与容器几何按不可变输入复用，输入一变即重建。</summary>
public sealed class SceneStaticCacheTests
{
    private static LyricLine Line(string id, long start)
        => LyricLine.Create(id, "AB", new(start, start + 1_000_000), TimingStatus.Edited,
            [LyricToken.Create(id + "-a", "A", new(start, start + 500_000), TimingStatus.Edited),
                LyricToken.Create(id + "-b", "B", new(start + 500_000, start + 1_000_000), TimingStatus.Edited)]);

    [Fact]
    public void 同一歌词与展示树复用定义和解析器_任一输入变化即重建()
    {
        var project = Project.Create("cache", Composition.Create(320, 180, FrameRate.Fps30),
            LyricsDocument.Create([Line("line-0", 0), Line("line-1", 2_000_000)]));
        var snapshot = ProjectSnapshot.FromProject(project);
        var definition = SceneDefinitionBuilder.Build(snapshot);
        // 旧实现每次调用都按全曲重建；同一不可变输入（含仅改名的新快照）必须复用。
        Assert.Same(definition, SceneDefinitionBuilder.Build(snapshot));
        Assert.Same(definition, SceneDefinitionBuilder.Build(snapshot with { Name = "renamed" }));
        Assert.Same(PresetSceneStyleProjection.Resolver(snapshot), PresetSceneStyleProjection.Resolver(snapshot));
        Assert.NotSame(PresetSceneStyleProjection.Resolver(snapshot),
            PresetSceneStyleProjection.Resolver(snapshot with { LyricStyle = snapshot.LyricStyle with { FontSizePx = 50 } }));

        var branch = snapshot.Lyrics.Branches[0];
        var moreLyrics = snapshot.Lyrics with { Branches = [branch with { Lines = branch.Lines.Add(Line("line-2", 4_000_000)) }] };
        var withLine = SceneDefinitionBuilder.Build(snapshot with { Lyrics = moreLyrics });
        Assert.Contains(withLine.Elements, element => element.Id == SceneDefinitionBuilder.TextId(branch.Id, "line-2"));
        Assert.DoesNotContain(definition.Elements, element => element.Id == SceneDefinitionBuilder.TextId(branch.Id, "line-2"));

        var root = snapshot.LyricPresentation.FindProgram(branch.Id)!.RootContainerId;
        var moreNodes = snapshot.LyricPresentation with
            { Nodes = snapshot.LyricPresentation.Nodes.Add(new() { Id = "extra-group", ParentId = root }) };
        var withNode = SceneDefinitionBuilder.Build(snapshot with { LyricPresentation = moreNodes });
        Assert.Contains(withNode.Elements, element => element.Id == "extra-group");
        Assert.DoesNotContain(SceneDefinitionBuilder.Build(snapshot).Elements, element => element.Id == "extra-group");
    }

    [Fact]
    public void 容器几何与父关系按定义复用_合成尺寸变化即重算()
    {
        var project = Project.Create("bind", Composition.Create(320, 180, FrameRate.Fps30),
            LyricsDocument.Create([Line("line-0", 0), Line("line-1", 2_000_000)]));
        var small = ProjectSnapshot.FromProject(project);
        var large = small with { Composition = Composition.Create(640, 360, FrameRate.Fps30) };
        var definition = SceneDefinitionBuilder.Build(small);
        Assert.Same(definition, SceneDefinitionBuilder.Build(large));
        Assert.Same(SceneDefinitionBuilder.ElementParents(definition), SceneDefinitionBuilder.ElementParents(definition));
        // 同一定义交替绑定两种合成尺寸：每次都必须取当前尺寸的容器几何，缓存不得沿用上一尺寸。
        foreach (var snapshot in new[] { small, large, small })
        {
            var sample = Assert.Single(LyricSceneSampler.SampleFrame(snapshot, 500_000).Lines);
            var expected = SceneContainerGeometryResolver.Resolve(definition, snapshot.Composition)[sample.ParentId];
            Assert.Equal(expected.Width, sample.ParentReferenceWidth);
            Assert.Equal(expected.Height, sample.ParentReferenceHeight);
            Assert.Equal(expected.WorldMatrix, sample.ParentMatrix);
        }
        Assert.NotEqual(SceneContainerGeometryResolver.Resolve(definition, small.Composition)[LyricSceneSampler.SampleFrame(small, 500_000).Lines[0].ParentId].Width,
            SceneContainerGeometryResolver.Resolve(definition, large.Composition)[LyricSceneSampler.SampleFrame(large, 500_000).Lines[0].ParentId].Width);
    }

    [Fact]
    public void 行基础样式按行对象记忆_工程样式或分支覆盖变化即重算()
    {
        var project = Project.Create("style", Composition.Create(1920, 1080, FrameRate.Fps30), LyricsDocument.Create([Line("line-0", 0)]));
        var snapshot = ProjectSnapshot.FromProject(project);
        var branch = snapshot.Lyrics.Branches[0];
        var larger = snapshot with { LyricStyle = snapshot.LyricStyle with { FontSizePx = snapshot.LyricStyle.FontSizePx * 2 } };
        var overridden = snapshot with { Lyrics = snapshot.Lyrics with { Branches = [branch with
            { StyleOverride = new LyricStyleOverride { FontSizePx = snapshot.LyricStyle.FontSizePx * 3 } }] } };
        // 三个快照共用同一行对象，记忆只能在工程样式与分支覆盖也相同时命中。
        Assert.Same(branch.Lines[0], larger.Lyrics.Branches[0].Lines[0]);
        Assert.Same(branch.Lines[0], overridden.Lyrics.Branches[0].Lines[0]);
        using var pipeline = new SkiaRenderPipeline();
        double Width(ProjectSnapshot value)
        {
            var corners = Assert.Single(pipeline.GetSceneFrame(value, 500_000).Lines).Geometry!.Corners();
            return corners.Max(static point => point.X) - corners.Min(static point => point.X);
        }
        double baseline = Width(snapshot);
        Assert.Equal(baseline * 2, Width(larger), baseline * 0.2);
        Assert.Equal(baseline * 3, Width(overridden), baseline * 0.3);
        Assert.Equal(baseline, Width(snapshot), 1e-6);
    }

    [Fact]
    public void 行与逐字几何按行对象记忆_原点或世界矩阵变化即重测_与新管线逐项相同()
    {
        // 局部缩放改变世界矩阵（原点不变）；换合成尺寸改变行原点与基线。三者都共用同一批行对象。
        var project = Project.Create("measure", Composition.Create(1920, 1080, FrameRate.Fps30),
            LyricsDocument.Create([Line("line-0", 0), Line("line-1", 2_000_000)]));
        var large = ProjectSnapshot.FromProject(project);
        var scaled = large with
        {
            LyricPresentation = large.LyricPresentation.WithDefaultPrograms(large.Lyrics) with
            {
                LineOverrides = [new() { BranchId = large.Lyrics.PrimaryBranchId, LineId = "line-0", LocalTransform = new() { Scale = .8 } }],
            },
        };
        var small = large with { Composition = Composition.Create(1280, 720, FrameRate.Fps30) };
        Assert.Same(large.Lyrics.Branches[0].Lines[0], small.Lyrics.Branches[0].Lines[0]);
        Assert.Same(large.Lyrics.Branches[0].Lines[0], scaled.Lyrics.Branches[0].Lines[0]);
        using var pipeline = new SkiaRenderPipeline();
        var first = pipeline.GetSceneFrame(large, 500_000);
        var again = pipeline.GetSceneFrame(large, 900_000);
        // 行静止：两次补算复用同一不可变测量结果（旧实现每次重排并重建身份字符串）。
        Assert.Same(first.TokenGeometries[0].Clusters[0], again.TokenGeometries[0].Clusters[0]);
        Assert.Same(first.LineGeometries[0].BaseGeometry, again.LineGeometries[0].BaseGeometry);
        foreach (var (snapshot, time) in new[] { (large, 100_000L), (scaled, 200_000L), (small, 200_000L), (large, 2_100_000L), (small, 2_500_000L), (large, 500_000L) })
        {
            using var fresh = new SkiaRenderPipeline();
            var actual = pipeline.GetSceneFrame(snapshot, time);
            var expected = fresh.GetSceneFrame(snapshot, time);
            Assert.Equal(expected.LineGeometries.Length, actual.LineGeometries.Length);
            for (int i = 0; i < expected.LineGeometries.Length; i++)
            {
                Assert.Equal(expected.LineGeometries[i].Target, actual.LineGeometries[i].Target);
                AssertGeometry(expected.LineGeometries[i].BaseGeometry, actual.LineGeometries[i].BaseGeometry);
                Assert.Equal(expected.LineGeometries[i].ProgressPath, actual.LineGeometries[i].ProgressPath);
            }
            Assert.Equal(expected.TokenGeometries.Length, actual.TokenGeometries.Length);
            for (int i = 0; i < expected.TokenGeometries.Length; i++)
            {
                var (want, got) = (expected.TokenGeometries[i], actual.TokenGeometries[i]);
                Assert.Equal(want.WithRuby, got.WithRuby);
                Assert.Equal(want.ProgressPath, got.ProgressPath);
                AssertGeometry(want.BaseGeometry, got.BaseGeometry);
                AssertGeometry(want.FamilyGeometry, got.FamilyGeometry);
                Assert.Equal(want.Clusters.Length, got.Clusters.Length);
                for (int c = 0; c < want.Clusters.Length; c++)
                {
                    Assert.Equal(want.Clusters[c].Target, got.Clusters[c].Target);
                    AssertGeometry(want.Clusters[c].Geometry, got.Clusters[c].Geometry);
                }
            }
        }
        Assert.NotEqual(pipeline.GetSceneFrame(large, 100_000).LineGeometries[0].BaseGeometry.BaseWorldMatrix,
            pipeline.GetSceneFrame(scaled, 200_000).LineGeometries[0].BaseGeometry.BaseWorldMatrix);
        Assert.Equal(pipeline.GetSceneFrame(large, 100_000).LineGeometries[0].BaseGeometry.AdvanceBounds,
            pipeline.GetSceneFrame(scaled, 200_000).LineGeometries[0].BaseGeometry.AdvanceBounds);
        Assert.NotEqual(pipeline.GetSceneFrame(large, 500_000).LineGeometries[0].BaseGeometry.AdvanceBounds,
            pipeline.GetSceneFrame(small, 500_000).LineGeometries[0].BaseGeometry.AdvanceBounds);
    }

    private static void AssertGeometry(Scripting.GeometrySample expected, Scripting.GeometrySample actual)
    {
        Assert.Equal(expected.AdvanceBounds, actual.AdvanceBounds);
        Assert.Equal(expected.InkBounds, actual.InkBounds);
        Assert.Equal(expected.Baseline, actual.Baseline);
        Assert.Equal(expected.BaseWorldMatrix, actual.BaseWorldMatrix);
        Assert.Equal(expected.DrawUnits.ToArray(), actual.DrawUnits.ToArray());
        Assert.Equal(expected.CanTransformIndependently, actual.CanTransformIndependently);
    }
}
