using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Tests.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class GlowPersistenceTests
{
    private static readonly GlowSpec Glow = new() { Color = new(12, 34, 56, 128), Opacity = .4f, BlurRadiusPx = 6, SpreadPx = 3 };

    [Fact]
    public void 辉光基线分支行元素缺失空值部分完整四态跨文件往返()
    {
        using TemporaryDirectory directory = new();
        var document = LyricsDocument.Create([LyricLine.Create("line", "甲乙丙丁", tokens:
        [
            LyricToken.Create("a", "甲"),
            LyricToken.Create("b", "乙", styleOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(null) }),
            LyricToken.Create("c", "丙", styleOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Opacity = .2f }) }),
            LyricToken.Create("d", "丁", styleOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(GlowSpecOverride.FromSpec(Glow)) }),
        ], styleOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { SpreadPx = 9 }) })]);
        document = document with { Branches = [document.Branches[0] with { StyleOverride = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { BlurRadiusPx = 4 }) } }] };
        var project = Project.Create("辉光", lyrics: document, lyricStyle: LyricStyle.Default with { Glow = Glow });
        var restored = RoundTrip(project, directory.Path);
        Assert.Equal(project, restored);
        Assert.Equal(Glow, restored.LyricStyle.Glow);
        Assert.Null(restored.Lyrics.Lines[0].Tokens[0].StyleOverride);
        Assert.True(restored.Lyrics.Lines[0].Tokens[1].StyleOverride!.Glow.IsSet);
        Assert.Null(restored.Lyrics.Lines[0].Tokens[1].StyleOverride!.Glow.Value);
        Assert.Equal(new GlowSpecOverride { Opacity = .2f }, restored.Lyrics.Lines[0].Tokens[2].StyleOverride!.Glow.Value);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(directory.Path, ProjectJson.ProjectFileName)))!;
        Assert.Equal(Project.CurrentSchemaVersion, json["schemaVersion"]!.GetValue<int>());
        Assert.Equal("#0C223880", json["lyricStyle"]!["unsung"]!["glow"]!["color"]!.GetValue<string>());
        var lyrics = JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!;
        Assert.Equal(LyricsDocument.CurrentSchemaVersion, lyrics["schemaVersion"]!.GetValue<int>());
        Assert.Equal(.2f, lyrics["branches"]![0]!["lines"]![0]!["tokens"]![2]!["styleOverride"]!["unsung"]!["glow"]!["opacity"]!.GetValue<float>());
    }

    [Theory]
    [InlineData("opacity", -1f)] [InlineData("opacity", 2f)]
    [InlineData("blurRadiusPx", -1f)] [InlineData("spreadPx", -1f)]
    public void 无效基线与部分覆盖辉光均报告样式错误(string field, float value)
    {
        using TemporaryDirectory directory = new();
        var project = Project.Create("非法辉光", lyrics: LyricsDocument.Create([LyricLine.Create("line", "甲", tokens:
            [LyricToken.Create("a", "甲", styleOverride: new() { Glow = OptionalValue.Set<GlowSpecOverride?>(new() { Opacity = .2f }) })])]),
            lyricStyle: LyricStyle.Default with { Glow = Glow });
        ProjectStore.Save(project, directory.Path);
        string path = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        var root = JsonNode.Parse(File.ReadAllText(path))!; root["lyricStyle"]!["unsung"]!["glow"]![field] = value;
        File.WriteAllText(path, root.ToJsonString());
        Assert.Contains(ProjectStore.Load(directory.Path).Report.Issues, i => i.Code == DiagnosticCode.InvalidStyleValue && i.Location == "lyricStyle.unsung.glow");
        ProjectStore.Save(project, directory.Path);
        var lyrics = JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!;
        lyrics["branches"]![0]!["lines"]![0]!["tokens"]![0]!["styleOverride"]!["unsung"]!["glow"]![field] = value;
        ProjectFileFixture.PublishLyrics(directory.Path, lyrics.ToJsonString());
        Assert.Contains(ProjectStore.Load(directory.Path).Report.Issues, i => i.Code == DiagnosticCode.InvalidStyleValue && i.Location!.EndsWith(".glow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[]")] [InlineData("{\"spreadPx\":\"bad\"}")] [InlineData("{\"unknown\":1}")]
    public void 辉光覆盖非法结构不静默忽略(string invalid)
    {
        using TemporaryDirectory directory = new();
        var project = Project.Create("结构", lyrics: LyricsDocument.Create([LyricLine.Create("line", "甲",
            styleOverride: new() { Opacity = .5f })]));
        ProjectStore.Save(project, directory.Path);
        var lyrics = JsonNode.Parse(ProjectFileFixture.ReadLyrics(directory.Path))!;
        lyrics["branches"]![0]!["lines"]![0]!["styleOverride"]!["unsung"] = new JsonObject { ["glow"] = JsonNode.Parse(invalid) };
        ProjectFileFixture.PublishLyrics(directory.Path, lyrics.ToJsonString());
        var loaded = ProjectStore.Load(directory.Path);
        Assert.True(loaded.Report.HasErrors);
        Assert.Contains(loaded.Report.Issues, i => i.Code == DiagnosticCode.InvalidStyleValue && i.Location!.EndsWith(".glow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void 前版工程及歌词拒绝读取且原字节保留(bool lyrics)
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Project.Create("旧格式"), directory.Path);
        string projectPath = Path.Combine(directory.Path, ProjectJson.ProjectFileName);
        var root = JsonNode.Parse(lyrics ? ProjectFileFixture.ReadLyrics(directory.Path) : File.ReadAllText(projectPath))!;
        root["schemaVersion"] = lyrics ? 17 : 28;
        if (lyrics) ProjectFileFixture.PublishLyrics(directory.Path, root.ToJsonString());
        else File.WriteAllText(projectPath, root.ToJsonString());
        byte[] before = File.ReadAllBytes(projectPath); string beforeLyrics = ProjectFileFixture.ReadLyrics(directory.Path);
        Assert.True(ProjectStore.Load(directory.Path).Report.HasErrors);
        Assert.Equal(before, File.ReadAllBytes(projectPath)); Assert.Equal(beforeLyrics, ProjectFileFixture.ReadLyrics(directory.Path));
    }

    [Fact]
    public void 倒计时完整辉光及文字标记部分与关闭覆盖往返()
    {
        using TemporaryDirectory directory = new();
        var project = LeadInTests.Create(out string id);
        var group = project.LyricPresentation.FindNode(id)!; var sequence = (CountdownSequenceContent)group.Content!;
        Assert.True(SceneTreeEditor.TrySetNode(project, group with { Content = sequence with { Appearance = sequence.Appearance with { Glow = Glow } } }, out project, out _));
        foreach (var entry in sequence.MarkNodeIds.Select((nodeId, index) => (nodeId, index)))
        {
            var node = project.LyricPresentation.FindNode(entry.nodeId)!; var mark = (TextMarkContent)node.Content!;
            GlowSpecOverride? patch = entry.index == 0 ? null : new() { SpreadPx = 5 };
            Assert.True(SceneTreeEditor.TrySetNode(project, node with { Content = mark with { AppearanceOverride = new() { Pending = new() { Glow = OptionalValue.Set<GlowSpecOverride?>(patch) } } } }, out project, out _));
        }
        Assert.Equal(project, RoundTrip(project, directory.Path));
    }

    [Fact]
    public void 元素辉光模板保存全部字段且只应用选中叶字段及旧库保留()
    {
        using TemporaryDirectory directory = new(); string path = StyleTemplateLibrary.ResolvePath(directory.Path);
        Assert.Contains(StyleTemplateLibrary.DirectoryName, path, StringComparison.Ordinal);
        var template = StyleTemplate.Create("glow", "辉光", StyleTemplateKind.Token, LyricStyle.Default with { Glow = Glow },
            [LyricStyleProperty.UnsungGlowColor, LyricStyleProperty.UnsungGlowSpreadPx]);
        StyleTemplateLibrary.Save(path, [template]); var loaded = StyleTemplateLibrary.Load(path);
        Assert.Empty(loaded.Issues); Assert.Equal(Glow, Assert.Single(loaded.Templates).Style.Glow);
        Assert.Equal(StyleTemplateLibrary.CurrentSchemaVersion, JsonNode.Parse(Encoding.UTF8.GetString(StyleTemplateLibrary.Encode([template])))!["schemaVersion"]!.GetValue<int>());
        var project = Project.Create("模板", lyrics: LyricsDocument.Create([LyricLine.Create("line", "甲", tokens: [LyricToken.Create("a", "甲")])]));
        Assert.True(StyleTemplateApplicator.TryApply(project, loaded.Templates[0], "line", "a", out var applied, out string reason), reason);
        Assert.Equal(new GlowSpecOverride { Color = Glow.Color, SpreadPx = Glow.SpreadPx }, applied.Lyrics.Lines[0].Tokens[0].StyleOverride!.Glow.Value);
        var oldIndex = JsonNode.Parse(StyleTemplateLibrary.Encode([template]))!;
        oldIndex["schemaVersion"] = StyleTemplateLibrary.CurrentSchemaVersion - 1;
        string old = oldIndex.ToJsonString();
        Assert.True(StyleTemplateLibrary.Deserialize(old).VersionMismatch);
        File.Delete(path); string oldPath = Path.Combine(directory.Path, "templates-v6", StyleTemplateLibrary.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!); File.WriteAllText(oldPath, old);
        Assert.True(StyleTemplateLibrary.Load(path).LegacyLibraryRetained); Assert.Equal(old, File.ReadAllText(oldPath));
    }

    private static Project RoundTrip(Project project, string directory)
    {
        ProjectStore.Save(project, directory); var result = ProjectStore.Load(directory);
        Assert.False(result.Report.HasErrors, result.Report.ToSummary()); return Assert.IsType<Project>(result.Project);
    }
}
