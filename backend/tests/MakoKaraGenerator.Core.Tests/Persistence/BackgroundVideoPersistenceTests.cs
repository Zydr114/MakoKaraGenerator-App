using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>视频背景的持久化与迁移测试（schema v7 → v8）。</summary>
public sealed class BackgroundVideoPersistenceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 视频背景保存后能读回()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        Project project = Project.Create(
            "工程",
            background: BackgroundSource.ForVideo(VideoPath(), BackgroundFit.Cover, BackgroundPlaybackMode.FreezeLastFrame));

        ProjectStore.Save(project, projectDirectory);
        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal(BackgroundKind.Video, result.Project.Background.Kind);
        Assert.Equal(Path.Combine(projectDirectory, result.Project.Background.Media!.RelativePath), result.Project.Background.Path);
        Assert.Equal("movie.mp4", result.Project.Background.Media.OriginalName);
        Assert.Equal(BackgroundFit.Cover, result.Project.Background.Fit);
        Assert.Equal(BackgroundPlaybackMode.FreezeLastFrame, result.Project.Background.Playback);
        Assert.False(result.Report.HasErrors);
    }

    [Fact]
    public void 不认识的播放方式回退循环并报告()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(
            Project.Create("工程", background: BackgroundSource.ForVideo(VideoPath())),
            projectDirectory);

        string path = Path.Combine(projectDirectory, "project.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["background"]!["playback"] = "倒退播放";
        File.WriteAllText(path, root.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal(BackgroundPlaybackMode.Loop, result.Project.Background.Playback);
        Assert.Contains(
            result.Report.Issues,
            issue => issue.Code == DiagnosticCode.InvalidBackgroundSource);
    }

    [Fact]
    public void 旧schema视频工程被拒绝()
    {
        // 模拟 v7 文件：去掉 playback 并把版本降回 7，让迁移链真的跑一遍。
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(
            Project.Create("工程", background: BackgroundSource.ForVideo(VideoPath())),
            projectDirectory);

        string path = Path.Combine(projectDirectory, "project.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["background"]!.AsObject().Remove("playback");
        root["schemaVersion"] = 7;
        File.WriteAllText(path, root.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }
    private string VideoPath()
    {
        string path = _directory.FilePath("movie.mp4"); File.WriteAllBytes(path, [1, 2, 3]); return path;
    }

}
