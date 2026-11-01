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
        var changed = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0), new(1, 1), new(0, 0)], style, [LyricStyleProperty.UnsungColor]);
        Assert.Same(project.LyricStyle, changed.LyricStyle);
        Assert.Equal(project.Lyrics.Lines[0].StyleOverride, changed.Lyrics.Lines[0].StyleOverride);
        Assert.Equal(style.UnsungColor, changed.Lyrics.Lines[0].Tokens[0].StyleOverride!.UnsungColor);
        Assert.Equal(style.UnsungColor, changed.Lyrics.Lines[1].Tokens[1].StyleOverride!.UnsungColor);
        Assert.Null(changed.Lyrics.Lines[0].Tokens[1].StyleOverride);
        Assert.Null(changed.Lyrics.Lines[0].Tokens[0].StyleOverride!.FontSizePx);
        Assert.Same(changed, StyleBatchEditor.Apply(changed, StyleTargetLevel.Token, [new(0, 0), new(1, 1)], style, [LyricStyleProperty.UnsungColor]));
    }

    [Fact]
    public void 非法目标或能力整批拒绝()
    {
        var project = CreateProject();
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(80)], LyricStyle.Default, [LyricStyleProperty.Scale]));
        Assert.Throws<ArgumentException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], LyricStyle.Default, [LyricStyleProperty.FontSizePx]));
        Assert.Throws<ArgumentException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], LyricStyle.Default, [], includeAnimation: true));
        Assert.Null(project.Lyrics.Lines[0].StyleOverride);
    }

    [Fact]
    public void 全部属性复制有映射且恢复继承会清空()
    {
        var style = LyricStyle.Default with { FontFamilyName = "Noto Sans CJK SC", FontSizePx = 91, Opacity = .6f, Scale = 1.3f,
            UnsungColor = new(200, 10, 30), SungColor = new(20, 230, 40), HorizontalAlignment = LyricHorizontalAlignment.Left,
            VerticalCenterRatio = .7, StrokeWidthPx = 3, Shadow = new ShadowSpec
            {
                Color = new RgbaColor(0, 0, 0, 160), AngleDegrees = Math.Atan2(5d, 2d) * 180d / Math.PI,
                DistancePx = (float)Math.Sqrt(29d), BlurRadiusPx = 4f,
            } };
        foreach (var property in LyricStylePropertyExtensions.All)
        {
            var overlay = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, [property]);
            var set = TokenStyleSupport.SetProperties(overlay);
            Assert.Contains(property, set);
            Assert.Equal(property is LyricStyleProperty.UnsungColor or LyricStyleProperty.UnsungGradientSpec or LyricStyleProperty.SungColor or LyricStyleProperty.SungGradientSpec ? 2 : property == LyricStyleProperty.GlowSpec ? 5 : 1, set.Count);
            Assert.False(overlay.IsEmpty);
            Assert.True(StyleBatchEditor.CopyProperties(overlay, style, [property], restoreInheritance: true).IsEmpty);
        }
        var full = StyleBatchEditor.CopyProperties(LyricStyleOverride.Empty, style, LyricStylePropertyExtensions.All);
        Assert.Equal(style, LyricStyleResolver.Resolve(LyricStyle.Default, lineOverride: full).Style);
    }

    [Fact]
    public void 明确关闭与恢复继承不同且不清除其他属性()
    {
        GradientSpec gradient = new() { Stops = [new GradientStop(0, new(255, 0, 0)), new GradientStop(1, new(0, 255, 0))] };
        var project = CreateProject() with { LyricStyle = LyricStyle.Default with { UnsungGradientSpec = gradient } };
        var off = StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(1)], LyricStyle.Default, [LyricStyleProperty.UnsungGradientSpec]);
        var overlay = off.Lyrics.Lines[0].StyleOverride!;
        Assert.True(overlay.UnsungGradientSpec.IsSet);
        Assert.Null(LyricStyleResolver.Resolve(off.LyricStyle, lineOverride: overlay).Style.UnsungGradientSpec);
        var restored = StyleBatchEditor.Apply(off, StyleTargetLevel.Line, [new(0)], LyricStyle.Default, [LyricStyleProperty.UnsungGradientSpec], restoreInheritance: true);
        Assert.False(restored.Lyrics.Lines[0].StyleOverride?.UnsungGradientSpec.IsSet ?? false);
        Assert.Equal(project.LyricStyle.UnsungGradientSpec, LyricStyleResolver.Resolve(restored.LyricStyle, lineOverride: restored.Lyrics.Lines[0].StyleOverride).Style.UnsungGradientSpec);
    }

    [Fact]
    public void 工程无变化保留预设标识与原实例()
    {
        var project = CreateProject() with { LyricStylePresetId = "custom" };
        Assert.Same(project, StyleBatchEditor.Apply(project, StyleTargetLevel.Project, [], project.LyricStyle, LyricStylePropertyExtensions.All));
    }

    [Fact]
    public void 动画与样式一起应用且拒绝非有限数()
    {
        var project = CreateProject();
        var animation = LineAnimation.Default with { StartOffsetPx = 75 };
        var changed = StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0), new(1)], project.LyricStyle with { FontSizePx = 80 }, [LyricStyleProperty.FontSizePx], true, animation);
        Assert.All(changed.Lyrics.Lines, line => Assert.Equal(animation, changed.LyricPresentation.FindLine(line.Id)!.EnterAnimation));
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Project, [], project.LyricStyle with { Scale = float.NaN }, [LyricStyleProperty.Scale]));
        Assert.Throws<ArgumentOutOfRangeException>(() => StyleBatchEditor.Apply(project, StyleTargetLevel.Line, [new(0)], project.LyricStyle, [], true, animation with { StartOpacity = float.PositiveInfinity }));
    }

    [Fact]
    public void 六种可选颜色三态保存重开不丢失()
    {
        using TemporaryDirectory directory = new();
        var project = CreateProject();
        var optional = new[] { LyricStyleProperty.UnsungGradientSpec, LyricStyleProperty.SungGradientSpec,
            LyricStyleProperty.StrokeColor, LyricStyleProperty.ShadowColor };
        var off = StyleBatchEditor.Apply(project, StyleTargetLevel.Token, [new(0, 0)], LyricStyle.Default, optional);
        ProjectStore.Save(off, directory.Path);
        string json = ProjectFileFixture.ReadLyrics(directory.Path);
        Assert.Contains("\"unsungFill\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sungFill\"", json, StringComparison.Ordinal);
        Assert.Contains("\"stroke\"", json, StringComparison.Ordinal);
        Assert.Contains("\"shadow\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unsungGradientStartColor", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shadowOffsetXPx", json, StringComparison.Ordinal);
        var loaded = ProjectStore.Load(directory.Path);
        Assert.False(loaded.Report.HasErrors);
        Assert.Equal(off.Lyrics, loaded.Project!.Lyrics);
        Assert.Null(loaded.Project.Lyrics.Lines[0].Tokens[1].StyleOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 两条旧版本迁移均保留空颜色继承语义(bool lyrics)
    {
        IReadOnlyList<IProjectSchemaMigration> migrations = lyrics
            ? ProjectSchemaMigrator.LyricsDefault
            : ProjectSchemaMigrator.Default;
        IProjectSchemaMigration optionalColorMigration = migrations.Single(
            migration => migration.Description.Contains("可选颜色", StringComparison.Ordinal));
        var document = JsonNode.Parse("""{"lines":[{"styleOverride":{"strokeColor":null,"shadowColor":"#FF0000FF"},"tokens":[{"styleOverride":{"unsungGradientStartColor":null}}]}]}""")!.AsObject();
        ProjectSchemaMigrator.Migrate(
            document,
            optionalColorMigration.FromVersion,
            optionalColorMigration.ToVersion,
            migrations);
        var line = document["lines"]![0]!;
        Assert.False(line["styleOverride"]!.AsObject().ContainsKey("strokeColor"));
        Assert.Equal("#FF0000FF", line["styleOverride"]!["shadowColor"]!.GetValue<string>());
        Assert.False(line["tokens"]![0]!["styleOverride"]!.AsObject().ContainsKey("unsungGradientStartColor"));
    }

    private static Project CreateProject() => Project.Create("批量", new Composition(), LyricsDocument.Create(
        Enumerable.Range(0, 2).Select(i => LyricLine.Create("line" + i, "夜空", new TimeRange(0, 2_000_000), TimingStatus.Edited,
            [LyricToken.Create($"t{i}a", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited),
             LyricToken.Create($"t{i}b", "空", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited)]))));
}
