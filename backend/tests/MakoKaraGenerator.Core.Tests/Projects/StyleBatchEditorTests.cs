using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Projects;

public sealed class StyleBatchEditorTests
{
    [Fact]
    public void 批量元素只写选定属性且去重并保持其他层()
    {
        var project = CreateProject();
        var style = project.LyricStyle with { UnsungColor = new(200, 10, 30), FontSizePx = 300 };
        var changed = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0), new(1, 1), new(0, 0)], style, [LyricStyleProperty.UnsungFill]);
        Assert.Same(project.LyricStyle, changed.LyricStyle);
        Assert.Equal(project.Lyrics.Lines[0].StyleOverride, changed.Lyrics.Lines[0].StyleOverride);
        Assert.Equal(style.UnsungColor, changed.Lyrics.Lines[0].Tokens[0].StyleOverride!.UnsungColor);
        Assert.Equal(style.UnsungColor, changed.Lyrics.Lines[1].Tokens[1].StyleOverride!.UnsungColor);
        Assert.Null(changed.Lyrics.Lines[0].Tokens[1].StyleOverride);
        Assert.Null(changed.Lyrics.Lines[0].Tokens[0].StyleOverride!.FontSizePx);
        Assert.Same(changed, StyleBatchEditor.Apply(changed, StyleTargetLevel.Token, [new(0, 0), new(1, 1)], style, [LyricStyleProperty.UnsungFill]));
    }

    [Fact]
    public void 非法目标或能力整批拒绝()
    {
        var project = CreateProject();
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(80)], LyricStyle.Default, [LyricStyleProperty.Scale]));
        Assert.Throws<ArgumentException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], LyricStyle.Default, [LyricStyleProperty.FontSizePx]));
        Assert.Null(project.Lyrics.Lines[0].StyleOverride);
    }

    [Fact]
    public void 全部属性复制有映射且恢复继承会清空()
    {
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 91, Opacity = .6f, Scale = 1.3f,
            UnsungColor = new(200, 10, 30), SungColor = new(20, 230, 40), HorizontalAlignment = LyricHorizontalAlignment.Left,
            VerticalCenterRatio = .7, Stroke = StrokeSpec.Create(new RgbaColor(255, 255, 255), 3f), Shadow = new ShadowSpec
            {
                Color = new RgbaColor(0, 0, 0, 160), AngleDegrees = Math.Atan2(5d, 2d) * 180d / Math.PI,
                DistancePx = (float)Math.Sqrt(29d), BlurRadiusPx = 4f,
            },
            Wipe = new() { FeatherRatio = .5d, Highlight = new() { WidthRatio = .3d } } };
        style = style with { Sung = style.Sung with { Glow = Followable.Set<GlowSpec?>(GlowSpec.Create(new RgbaColor(255, 220, 0), .7f, 6f, 1f)) } };
        foreach (var property in LyricStylePropertyExtensions.All)
        {
            var overlay = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, [property]);
            Assert.Contains(property, TokenStyleSupport.SetProperties(overlay));
            Assert.False(overlay.IsEmpty);
            Assert.True(StyleBatchEditor.CopyProperties(overlay, style, [property], restoreInheritance: true).IsEmpty);
        }
        var full = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, LyricStylePropertyExtensions.All);
        Assert.Equal(style, LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: full).Style);
    }

    [Fact]
    public void 整体项与分量同选时已唱保持跟随而只选分量时写成显式值()
    {
        var style = LyricStyle.Default with { Shadow = ShadowSpec.Create(new RgbaColor(0, 0, 0), .5f, 45d, 4f, 3f) };
        var full = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, LyricStylePropertyExtensions.All);
        Assert.True(full.Sung!.Shadow.Value.FollowsUnsung);
        Assert.True(full.Sung.Glow.Value.FollowsUnsung);
        Assert.True(LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: full).Style.Sung.Shadow.FollowsUnsung);
        var leaf = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, [LyricStyleProperty.SungShadowColor]);
        Assert.False(leaf.Sung!.Shadow.Value.FollowsUnsung);
        Assert.Equal(style.Shadow!.Color, leaf.Sung.Shadow.Value.Value!.Color);
    }

    [Fact]
    public void 明确关闭与恢复继承不同且不清除其他属性()
    {
        ShadowSpec shadow = ShadowSpec.Create(new RgbaColor(0, 0, 0), .5f, 45d, 4f, 3f);
        var project = CreateProject() with { LyricStyle = LyricStyle.Default with { Shadow = shadow } };
        var off = StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(1)], LyricStyle.Default, [LyricStyleProperty.UnsungShadow]);
        var overlay = off.Lyrics.Lines[0].StyleOverride!;
        Assert.True(overlay.Shadow.IsSet);
        Assert.Null(LyricStyleResolver.Resolve(off.LyricStyle, lineOverride: overlay).Style.Shadow);
        var restored = StyleBatchEditor.Apply(off, StyleTargetLevel.Line, [new(0)], LyricStyle.Default, [LyricStyleProperty.UnsungShadow], restoreInheritance: true);
        Assert.False(restored.Lyrics.Lines[0].StyleOverride?.Shadow.IsSet ?? false);
        Assert.Equal(shadow, LyricStyleResolver.Resolve(restored.LyricStyle, lineOverride: restored.Lyrics.Lines[0].StyleOverride).Style.Shadow);
        Assert.True(restored.Lyrics.Lines[1].StyleOverride!.Shadow.IsSet);
    }

    [Fact]
    public void 工程无变化保留预设标识与原实例()
    {
        var project = CreateProject() with { LyricStylePresetId = "custom" };
        Assert.Same(project, StyleBatchEditor.Apply(project, StyleTargetLevel.Project, [], project.LyricStyle, LyricStylePropertyExtensions.All));
    }

    [Fact]
    public void 样式批量应用拒绝非有限数()
    {
        var project = CreateProject();
        var changed = StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(1)], project.LyricStyle with { FontSizePx = 80 }, [LyricStyleProperty.FontSizePx]);
        Assert.All(changed.Lyrics.Lines, line => Assert.Equal(80f, line.StyleOverride!.FontSizePx));
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Project, [], project.LyricStyle with { Scale = float.NaN }, [LyricStyleProperty.Scale]));
    }

    [Fact]
    public void 关闭与跟随的元素覆盖保存重开不丢失()
    {
        using TemporaryDirectory directory = new();
        var project = CreateProject();
        var optional = new[] { LyricStyleProperty.UnsungStrokes, LyricStyleProperty.SungStrokes,
            LyricStyleProperty.UnsungShadow, LyricStyleProperty.SungGlow };
        var off = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], LyricStyle.Default, optional);
        ProjectStore.Save(off, directory.Path);
        string json = ProjectFileFixture.ReadLyrics(directory.Path);
        Assert.Contains("\"strokes\"", json, StringComparison.Ordinal);
        Assert.Contains("\"shadow\"", json, StringComparison.Ordinal);
        Assert.Contains("\"follow\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unsungGradientStartColor", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowOffsetXPx", json, StringComparison.Ordinal);
        var loaded = ProjectStore.Load(directory.Path);
        Assert.False(loaded.Report.HasErrors);
        Assert.Equal(off.Lyrics, loaded.Project!.Lyrics);
        Assert.Null(loaded.Project.Lyrics.Lines[0].Tokens[1].StyleOverride);
    }

    private static Project CreateProject() => Project.Create("批量", new Composition(), LyricsDocument.Create(
        Enumerable.Range(0, 2).Select(i => LyricLine.Create("line" + i, "夜空", new TimeRange(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create($"t{i}a", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited),
             LyricToken.Create($"t{i}b", "空", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited)]))));
}
