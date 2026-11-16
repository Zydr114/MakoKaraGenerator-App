using System.Security.Cryptography;
using MakoKaraGenerator.Core.Diagnostics;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>音频路径随工程保存与打开的测试（schema v3）。</summary>
public sealed class ProjectMediaPersistenceTests
{
    [Fact]
    public void 工程目录内的音频也固定为内容地址并可重开()
    {
        using TemporaryDirectory directory = new();
        string projectDirectory = Path.Combine(directory.Path, "song.mkgproj");
        string assets = Path.Combine(projectDirectory, "assets");

        Directory.CreateDirectory(assets);

        string audioPath = Path.Combine(assets, "audio.mp3");

        File.WriteAllBytes(audioPath, [0x00]);

        ProjectStore.Save(Project.Create("歌", audioPath: audioPath), projectDirectory);

        string json = File.ReadAllText(Path.Combine(projectDirectory, "project.json"));

        // 文件里是相对路径：整个工程目录可以搬走。
        Assert.Contains("data/media/" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(audioPath))) + ".media", json, StringComparison.Ordinal);

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.True(result.Loaded);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDirectory, result.Project!.AudioMedia!.RelativePath)), result.Project.AudioPath);
        Assert.Equal("audio.mp3", result.Project.AudioMedia.OriginalName);
    }

    [Fact]
    public void 工程目录外的音频固定到独立受管资源()
    {
        using TemporaryDirectory directory = new();
        string projectDirectory = Path.Combine(directory.Path, "song.mkgproj");
        string outside = Path.Combine(directory.Path, "music", "song.mp3");

        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllBytes(outside, [0x00]);

        ProjectStore.Save(Project.Create("歌", audioPath: outside), projectDirectory);

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.Equal(Path.GetFullPath(Path.Combine(projectDirectory, result.Project!.AudioMedia!.RelativePath)), result.Project.AudioPath);
        Assert.Equal(File.ReadAllBytes(outside), File.ReadAllBytes(result.Project.AudioPath!));
        Assert.Equal("song.mp3", result.Project.AudioMedia.OriginalName);
    }

    [Fact]
    public void 没有音频的工程读回来也是没有()
    {
        using TemporaryDirectory directory = new();

        ProjectStore.Save(Project.Create("歌"), directory.Path);

        ProjectLoadResult result = ProjectStore.Load(directory.Path);

        Assert.True(result.Loaded);
        Assert.Null(result.Project!.AudioPath);
        Assert.DoesNotContain("audioPath", File.ReadAllText(Path.Combine(directory.Path, "project.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void 无程序版本的旧工程被拒绝且不改写()
    {
        using TemporaryDirectory directory = new();

        // 手写一份 v2 文件：样式字段齐全、没有音频路径。
        File.WriteAllText(
            Path.Combine(directory.Path, "project.json"),
            """
            {
              "schemaVersion": 2,
              "name": "旧工程",
              "composition": {
                "canvasWidth": 1920,
                "canvasHeight": 1080,
                "frameRate": { "numerator": 30, "denominator": 1 }
              }
            }
            """);

        ProjectLoadResult result = ProjectStore.Load(directory.Path);

        Assert.False(result.Loaded);
        Assert.True(result.Report.Contains(DiagnosticCode.ApplicationVersionMismatch));

        // 读取不得改写用户的文件。
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(Path.Combine(directory.Path, "project.json")), StringComparison.Ordinal);
    }

    [Fact]
    public void 空白音频路径拒绝加载而不丢失引用()
    {
        using TemporaryDirectory directory = new();

        ProjectStore.Save(Project.Create("空白路径"), directory.Path);
        string path = Path.Combine(directory.Path, "project.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["audioPath"] = "   ";
        File.WriteAllText(path, root.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(directory.Path);

        Assert.False(result.Loaded);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidMediaResource && issue.Severity == IssueSeverity.Error);
    }

    [Fact]
    public void 未登记的相对媒体路径拒绝加载()
    {
        using TemporaryDirectory directory = new();
        string projectDirectory = Path.Combine(directory.Path, "song.mkgproj");

        Directory.CreateDirectory(projectDirectory);

        ProjectStore.Save(Project.Create("带相对路径"), projectDirectory);
        string path = Path.Combine(projectDirectory, "project.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;
        root["audioPath"] = Path.Combine("assets", "audio.mp3");
        File.WriteAllText(path, root.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.Null(result.Project);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidMediaResource);
    }
}
