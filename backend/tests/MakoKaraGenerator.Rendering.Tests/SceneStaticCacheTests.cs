using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

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
}
