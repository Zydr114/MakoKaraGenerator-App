using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using System.Text.Json.Nodes;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>两态外观、「跟随未唱」、多层描边与扫字样式的持久化（P3 设计 §10.5）。</summary>
public sealed class AppearancePersistenceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 两态外观的跟随显式与关闭及多层描边和扫字样式往返无损()
    {
        StrokeLayers strokes = StrokeLayers.From([
            StrokeSpec.Create(new RgbaColor(255, 255, 255), 3f),
            StrokeSpec.Create(new RgbaColor(20, 30, 40, 200), 5f, StrokeJoin.Miter),
        ]);
        LyricStyle style = LyricStyle.Default with
        {
            Unsung = LyricStyle.Default.Unsung with
            {
                Strokes = strokes,
                Shadow = ShadowSpec.Create(new RgbaColor(0, 0, 0), 0.5f, 45d, 4f, 3f),
            },
            Sung = LyricStyle.Default.Sung with
            {
                Strokes = Followable.Set(StrokeLayers.Empty),
                Glow = Followable.Set<GlowSpec?>(GlowSpec.Create(new RgbaColor(255, 200, 0), 0.8f, 6f, 1f)),
            },
            Wipe = new() { FeatherRatio = 0.5d, Highlight = new() { WidthRatio = 0.3d, Opacity = 0.6f } },
        };

        Project reloaded = RoundTrip(Project.Create("两态外观", lyricStyle: style));

        Assert.Equal(style, reloaded.LyricStyle);
        Assert.True(reloaded.LyricStyle.Sung.Shadow.FollowsUnsung);
        Assert.Empty(reloaded.LyricStyle.ResolveSung().Strokes);
        Assert.Equal(style.Unsung.Shadow, reloaded.LyricStyle.ResolveSung().Shadow);
        JsonObject sung = ReadProject()["lyricStyle"]!["sung"]!.AsObject();
        Assert.Equal("set", (string?)sung["strokes"]!["kind"]);
        Assert.Empty(sung["strokes"]!["value"]!.AsArray());
        Assert.Equal("follow", (string?)sung["shadow"]!["kind"]);
        Assert.Equal(2, ReadProject()["lyricStyle"]!["unsung"]!["strokes"]!.AsArray().Count);
    }

    [Fact]
    public void 覆盖中的跟随显式关闭与扫字高亮跨行和元素往返()
    {
        LyricStyleOverride lineOverride = new()
        {
            Sung = new() { Shadow = OptionalValue.Set(Followable.Set<ShadowSpecOverride?>(null)) },
            Wipe = new() { FeatherRatio = 1d, Highlight = OptionalValue.Set<WipeHighlightOverride?>(new() { Opacity = 0.4f }) },
        };
        LyricStyleOverride tokenOverride = new()
        {
            Sung = new() { Shadow = OptionalValue.Set(Followable.Follow<ShadowSpecOverride?>()) },
            Wipe = new() { Highlight = OptionalValue.Set<WipeHighlightOverride?>(null) },
        };
        LyricLine line = LyricLine.Create("l1", "夜", tokens: [LyricToken.Create("t1", "夜", styleOverride: tokenOverride)],
            styleOverride: lineOverride);
        LyricStyle baseline = LyricStyle.Default with
        {
            Unsung = LyricStyle.Default.Unsung with { Shadow = ShadowSpec.Create(new RgbaColor(0, 0, 0), 1f, 90d, 3f, 2f) },
        };

        Project reloaded = RoundTrip(Project.Create("覆盖跟随", lyrics: LyricsDocument.Create([line]), lyricStyle: baseline));

        LyricLine row = reloaded.Lyrics.Lines[0];
        Assert.Equal(lineOverride, row.StyleOverride);
        Assert.Equal(tokenOverride, row.Tokens[0].StyleOverride);
        LyricStyle lineStyle = LyricStyleResolver.Resolve(reloaded.LyricStyle, lineOverride: row.StyleOverride).Style;
        Assert.Null(lineStyle.ResolveSung().Shadow);
        Assert.Equal(0.4f, lineStyle.Wipe.Highlight!.Opacity);
        LyricStyle tokenStyle = LyricStyleResolver.Resolve(reloaded.LyricStyle, lineOverride: row.StyleOverride,
            tokenOverride: row.Tokens[0].StyleOverride).Style;
        Assert.Equal(baseline.Unsung.Shadow, tokenStyle.ResolveSung().Shadow);
        Assert.Null(tokenStyle.Wipe.Highlight);
        Assert.Equal(1d, tokenStyle.Wipe.FeatherRatio);
    }

    [Theory]
    [InlineData("""{ "kind": "maybe" }""")]
    [InlineData("""{ "kind": "follow", "value": [] }""")]
    [InlineData("""{ "value": [] }""")]
    [InlineData("""[]""")]
    public void 已唱描边的跟随写法不合法时拒绝打开(string strokes)
    {
        ProjectStore.Save(Project.Create("非法跟随"), _directory.Path);
        JsonObject project = ReadProject();
        project["lyricStyle"]!["sung"]!["strokes"] = JsonNode.Parse(strokes);
        File.WriteAllText(ProjectPath, project.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.Contains(result.Report.Issues, static issue => issue.Code == DiagnosticCode.InvalidStyleValue);
    }

    [Theory]
    [InlineData("widthPx", "0")]
    [InlineData("widthPx", "-1")]
    [InlineData("join", "\"round\"")]
    [InlineData("position", "\"Outside\"")]
    public void 描边层的宽度连接方式或多余字段不合法时拒绝打开(string field, string value)
    {
        LyricStyle style = LyricStyle.Default with
        {
            Unsung = LyricStyle.Default.Unsung with { Strokes = StrokeLayers.FromStroke(StrokeSpec.Create(new RgbaColor(255, 255, 255), 2f)) },
        };
        ProjectStore.Save(Project.Create("非法描边", lyricStyle: style), _directory.Path);
        JsonObject project = ReadProject();
        project["lyricStyle"]!["unsung"]!["strokes"]![0]![field] = JsonNode.Parse(value);
        File.WriteAllText(ProjectPath, project.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(_directory.Path);

        Assert.Null(result.Project);
        Assert.Contains(result.Report.Issues, static issue => issue.Code == DiagnosticCode.InvalidStyleValue);
    }

    private string ProjectPath => Path.Combine(_directory.Path, ProjectJson.ProjectFileName);

    private JsonObject ReadProject() => JsonNode.Parse(File.ReadAllText(ProjectPath))!.AsObject();

    private Project RoundTrip(Project project)
    {
        ProjectStore.Save(project, _directory.Path);
        ProjectLoadResult result = ProjectStore.Load(_directory.Path);
        Assert.NotNull(result.Project);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary());
        return result.Project;
    }
}
