using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>行进入动画的持久化与迁移测试（工程 v9 / 歌词 v5）。</summary>
public sealed class LineAnimationPersistenceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 工程默认与行级覆盖都能往返()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        LineAnimation lineAnimation = new()
        {
            DurationUs = 250_000,
            Easing = AnimationEasing.EaseInOut,
            StartOpacity = 0.2f,
            StartScale = 1.4f,
            StartOffsetPx = -18f,
        };

        Project project = Project.Create(
            "工程",
            lyrics: LyricsDocument.Create(
            [
                LyricLine.Create("l1", "第一行", TimeRange.Create(0, 1_000_000), TimingStatus.Edited),
                LyricLine.Create("l2", "第二行", TimeRange.Create(1_000_000, 2_000_000), TimingStatus.Edited),
            ]),
            animation: LineAnimation.Default).WithLinePresentation("l2", animation: lineAnimation);

        ProjectStore.Save(project, projectDirectory);
        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal(LineAnimation.Default, result.Project.EnterAnimation);
        Assert.Null(result.Project.LyricPresentation.FindLine("l1")?.EnterAnimation);
        Assert.Equal(lineAnimation, result.Project.LyricPresentation.FindLine("l2")?.EnterAnimation);
        Assert.False(result.Report.HasErrors);
    }

    [Fact]
    public void 没有动画时不写出空对象()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(Project.Create("工程"), projectDirectory);

        JsonNode projectJson = JsonNode.Parse(File.ReadAllText(Path.Combine(projectDirectory, "project.json")))!;

        Assert.Null(projectJson["enterAnimation"]);
    }

    [Fact]
    public void 不认识的缓动回退默认并报告()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(
            Project.Create(
                "工程",
                lyrics: LyricsDocument.Create([LyricLine.Create("l1", "行", TimeRange.Create(0, 1_000_000), TimingStatus.Edited)]),
                animation: LineAnimation.Default),
            projectDirectory);

        string path = Path.Combine(projectDirectory, "project.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["enterAnimation"]!["easing"] = "弹跳";
        File.WriteAllText(path, root.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal(AnimationEasing.EaseOut, result.Project.EnterAnimation!.Easing);
        Assert.Contains(result.Report.Issues, static issue => issue.Code == DiagnosticCode.InvalidLineAnimation);
    }

    [Fact]
    public void 旧版本动画文件被拒绝()
    {
        // 工程 v8 与 歌词 v4 都不认识动画字段：迁移链跑完之后应当是"没有动画"。
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(
            Project.Create(
                "工程",
                lyrics: LyricsDocument.Create(
                [
                    LyricLine.Create("l1", "行", TimeRange.Create(0, 1_000_000), TimingStatus.Edited),
                ]),
                animation: LineAnimation.Default),
            projectDirectory);

        string projectPath = Path.Combine(projectDirectory, "project.json");
        JsonNode projectRoot = JsonNode.Parse(File.ReadAllText(projectPath))!;
        projectRoot.AsObject().Remove("enterAnimation");
        projectRoot["schemaVersion"] = 8;
        File.WriteAllText(projectPath, projectRoot.ToJsonString());

        string lyricsPath = ProjectFileFixture.LyricsPath(projectDirectory);
        JsonNode lyricsRoot = JsonNode.Parse(File.ReadAllText(lyricsPath))!;
        lyricsRoot["schemaVersion"] = 4;
        lyricsRoot["branches"]![0]!["lines"]![0]!.AsObject().Remove("enterAnimation");
        ProjectFileFixture.PublishLyrics(projectDirectory, lyricsRoot.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }
}
