using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>样式解析与预设投影行按（行，文字对象，片段，实际父链）记忆；结果须与不带记忆的全新解析逐项相同。</summary>
public sealed class PresetStyleMemoTests
{
    private static (ProjectSnapshot Snapshot, string[] Slots) Fixture()
    {
        var source = LeadInFixture.Create(slots: true, crossClip: true);
        var program = source.LyricPresentation.FindProgram("primary")!;
        var slots = source.LyricPresentation.FindNode(program.DefaultContainerId)!.Layout!.SlotIds.ToArray();
        Assert.True(slots.Length >= 2);
        Assert.True(PresetApplicationsEditor.TryApply(source, StyleTemplate.Create("slot", "slot", StyleTemplateKind.Composite,
            source.LyricStyle with { FontSizePx = 52, SungColor = new(30, 240, 90, 255) }, [LyricStyleProperty.FontSizePx, LyricStyleProperty.SungFill]),
            [new(ProjectEffectBindingKind.Node, id: slots[0], includeDescendants: true)], null, out var applied, out var reason), reason);
        Assert.True(PresetApplicationsEditor.TryApply(applied, StyleTemplate.Create("clip", "clip", StyleTemplateKind.Composite,
            source.LyricStyle with { UnsungColor = new(200, 70, 180, 255) }, [LyricStyleProperty.UnsungFill]),
            [new(ProjectEffectBindingKind.Clips, ids: ["chorus-clip"])], null, out applied, out reason), reason);
        return (ProjectSnapshot.FromProject(applied), slots);
    }

    private static PresetStyleResolver Fresh(ProjectSnapshot snapshot)
        => new(snapshot.LyricStyle, snapshot.Lyrics, snapshot.PresetApplications);

    [Fact]
    public void 解析记忆区分文字对象片段与父链_与全新解析相同()
    {
        var (snapshot, slots) = Fixture();
        var line = snapshot.Lyrics.AllLines.First(static line => !line.Tokens.IsEmpty);
        string root = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics).FindProgram("primary")!.RootContainerId;
        var resolver = Fresh(snapshot);
        IReadOnlyList<string>?[] paths = [null, new[] { root }, new[] { root, slots[0] }, new List<string> { root, slots[1] }];
        foreach (var path in paths)
            foreach (string? clip in new[] { null, "chorus-clip" })
                foreach (string? token in new[] { null, line.Tokens[0].Id })
                {
                    var first = resolver.ResolveLyrics(line.Id, token, clip, path);
                    // 同值的另一路径实例（含可变列表）命中同一结果；结果与从未记忆过的解析器逐项相同。
                    Assert.Same(first, resolver.ResolveLyrics(line.Id, token, clip, path?.ToList()));
                    var expected = Fresh(snapshot).ResolveLyrics(line.Id, token, clip, path);
                    Assert.Equal(expected.Style, first.Style);
                    Assert.Equal(expected.Sources, first.Sources);
                }
        // 父链确实改变结果：槽位预设只作用于经过该槽位的路径，片段预设只作用于该片段。
        Assert.NotEqual(resolver.ResolveLyrics(line.Id, null, null, [root, slots[0]]).Style,
            resolver.ResolveLyrics(line.Id, null, null, [root, slots[1]]).Style);
        Assert.NotEqual(resolver.ResolveLyrics(line.Id, null, "chorus-clip", [root]).Style, resolver.ResolveLyrics(line.Id, null, null, [root]).Style);
    }

    [Fact]
    public void 预设投影行按源行与实际父链复用_换槽位即按新父链解析()
    {
        var (snapshot, slots) = Fixture();
        int checkedLines = 0;
        foreach (long time in new long[] { 0, 11_000_000, 13_000_000, 18_000_000, 40_500_000, 51_000_000 })
        {
            var raw = LyricSceneSampler.SampleFrame(snapshot, time);
            var applied = PresetSceneStyleProjection.ApplyLines(snapshot, raw);
            var again = PresetSceneStyleProjection.ApplyLines(snapshot, LyricSceneSampler.SampleFrame(snapshot, time));
            for (int i = 0; i < raw.Lines.Length; i++)
            {
                var sample = raw.Lines[i];
                if (sample.ExitReplica is not null) continue;
                Assert.Same(applied.Lines[i].Frame.Line, again.Lines[i].Frame.Line);
                AssertProjected(snapshot, raw, sample, applied.Lines[i].Frame.Line!);
                // 同一源行改挂到另一个槽位：不得沿用旧父链的投影行。
                foreach (string slot in slots)
                {
                    var moved = raw with { Lines = raw.Lines.SetItem(i, sample with { ParentNodeId = slot }) };
                    AssertProjected(snapshot, moved, moved.Lines[i], PresetSceneStyleProjection.ApplyLines(snapshot, moved).Lines[i].Frame.Line!);
                }
                checkedLines++;
            }
        }
        Assert.True(checkedLines > 0);
    }

    private static void AssertProjected(ProjectSnapshot snapshot, SceneFrame frame, SceneLineSample sample, Core.Lyrics.LyricLine projected)
    {
        var resolver = Fresh(snapshot);
        var path = PresetSceneStyleProjection.NodePath(snapshot, sample.ElementId, frame);
        var lineStyle = resolver.ResolveLyrics(sample.Frame.Line!.Id, null, sample.DisplayClipId, path).Style;
        Assert.Equal(lineStyle, LyricStyleResolver.Resolve(snapshot.LyricStyle, lineOverride: projected.StyleOverride).Style);
        foreach (var token in projected.Tokens)
        {
            var tokenStyle = resolver.ResolveLyrics(sample.Frame.Line.Id, token.Id, sample.DisplayClipId, path).Style;
            Assert.Equal(tokenStyle == lineStyle ? null : StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, tokenStyle,
                TokenStyleSupport.SupportedAtTokenLevel), token.StyleOverride);
        }
    }
}
