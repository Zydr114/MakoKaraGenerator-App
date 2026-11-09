using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Rendering.Skia;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Tests;

/// <summary>逐帧基线只重算在场行，结果须与带场景完整重建的基线逐目标相同。</summary>
public sealed class FrameBaselineEquivalenceTests
{
    [Fact]
    public void 由会话基线派生的本帧基线与完整重建逐目标一致()
    {
        var source = LeadInFixture.Create(slots: true, crossClip: true);
        var program = source.LyricPresentation.FindProgram("primary")!;
        var slot = source.LyricPresentation.FindNode(program.DefaultContainerId)!.Layout!.SlotIds[0];
        Assert.True(PresetApplicationsEditor.TryApply(source, StyleTemplate.Create("slot", "slot", StyleTemplateKind.Composite,
            source.LyricStyle with { FontSizePx = 52, SungColor = new(30, 240, 90, 255) }, [LyricStyleProperty.FontSizePx, LyricStyleProperty.SungColor]),
            [new(ProjectEffectBindingKind.Node, id: slot, includeDescendants: true)], null, out var applied, out var reason), reason);
        Assert.True(PresetApplicationsEditor.TryApply(applied, StyleTemplate.Create("clip", "clip", StyleTemplateKind.Composite,
            source.LyricStyle with { UnsungColor = new(200, 70, 180, 255) }, [LyricStyleProperty.UnsungColor]),
            [new(ProjectEffectBindingKind.Clips, ids: ["chorus-clip"])], null, out applied, out reason), reason);
        var snapshot = ProjectSnapshot.FromProject(applied);
        var session = StaticStyleProjection.CreateBaseline(snapshot);
        var tree = snapshot.LyricPresentation.WithDefaultPrograms(snapshot.Lyrics);
        var marks = tree.Nodes.Where(static node => node.Content is TextMarkContent).Select(static node => node.Id).ToArray();
        var groups = SceneDefinitionBuilder.Build(snapshot).Elements.Where(static node => node.Kind == SceneElementKind.Group).Select(static node => node.Id).ToArray();
        Assert.NotEmpty(marks);
        using var pipeline = new SkiaRenderPipeline();
        int differing = 0;
        foreach (long time in new long[] { 0, 11_000_000, 13_000_000, 18_000_000, 40_500_000, 51_000_000 })
        {
            var scene = pipeline.GetSceneFrame(snapshot, time);
            var expected = StaticStyleProjection.CreateBaseline(snapshot, scene: scene);
            foreach (var actual in new[] { StaticStyleProjection.CreateFrameBaseline(session, snapshot, null, scene),
                StaticStyleProjection.CreateFrameBaseline(session, snapshot, null, scene,
                    StaticStyleProjection.MarkStyles(snapshot, scene), StaticStyleProjection.LoadedSourceStyles(snapshot, scene)) })
            {
                foreach (var line in snapshot.Lyrics.AllLines)
                {
                    Assert.Equal(expected.Get(new Fx.LineId(line.Id)), actual.Get(new Fx.LineId(line.Id)));
                    foreach (var token in line.Tokens) Assert.Equal(expected.Get(new Fx.TokenId(token.Id)), actual.Get(new Fx.TokenId(token.Id)));
                }
                foreach (var mark in marks)
                {
                    Fx.TextMarkTarget target = new(new(mark), new("cue"), new("binding"));
                    Assert.Equal(expected.Get(target), actual.Get(target));
                }
                foreach (var group in groups) Assert.Equal(expected.Get(new Fx.GroupTarget(new(group))), actual.Get(new Fx.GroupTarget(new(group))));
                foreach (var sample in scene.Lines)
                {
                    Fx.LineTarget line = new(new(sample.Frame.Line!.Id), new(sample.BindingId!));
                    Assert.Equal(expected.Get(line), actual.Get(line));
                    foreach (var token in sample.Frame.Line.Tokens)
                    {
                        Fx.TokenFamilyTarget family = new(new(token.Id), new(sample.BindingId!));
                        Assert.Equal(expected.Get(family), actual.Get(family));
                    }
                }
            }
            // 槽位／片段预设让在场行的源外观不同于不带场景的会话基线，确认派生确实替换了这些行。
            differing += scene.Lines.Count(sample => expected.Get(new Fx.LineId(sample.Frame.Line!.Id)) != session.Get(new Fx.LineId(sample.Frame.Line!.Id)));
        }
        Assert.True(differing > 0);
    }
}
