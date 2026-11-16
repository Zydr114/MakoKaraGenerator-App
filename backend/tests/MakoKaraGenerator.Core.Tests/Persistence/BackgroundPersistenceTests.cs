using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>背景源的持久化、迁移与资源收集测试（roadmap `EXPORT-04`）。</summary>
public sealed class BackgroundPersistenceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 背景图片保存后能读回()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string imagePath = WriteImage(Path.Combine(projectDirectory, "assets"), "bg.png");

        Project project = Project.Create(
            "工程",
            background: new BackgroundSource
            {
                Kind = BackgroundKind.Image,
                Path = imagePath,
                Fit = BackgroundFit.Cover,
            });

        ProjectStore.Save(project, projectDirectory);
        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal(BackgroundKind.Image, result.Project.Background.Kind);
        Assert.Equal(BackgroundFit.Cover, result.Project.Background.Fit);
        Assert.Equal(Path.GetFullPath(Path.Combine(projectDirectory, result.Project.Background.Media!.RelativePath)), result.Project.Background.Path);
        Assert.Equal("bg.png", result.Project.Background.Media.OriginalName);
        Assert.False(result.Report.HasErrors);
    }

    [Fact]
    public void 工程目录内的图片写相对路径()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string imagePath = WriteImage(Path.Combine(projectDirectory, "assets"), "bg.png");

        ProjectStore.Save(
            Project.Create("工程", background: BackgroundSource.ForImage(imagePath)),
            projectDirectory);

        JsonNode document = JsonNode.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "project.json")))!;

        Assert.Equal(
            "data/media/" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(imagePath))) + ".media",
            document["background"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public void 旧schema的工程被拒绝而不推测背景()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        ProjectStore.Save(Project.Create("工程"), projectDirectory);

        // 手写一份 v5 的 project.json：那时还没有背景字段。
        JsonObject document = JsonNode.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "project.json")))!.AsObject();
        document["schemaVersion"] = 5;
        document.Remove("background");

        File.WriteAllText(
            Path.Combine(projectDirectory, "project.json"),
            document.ToJsonString());

        ProjectLoadResult result = ProjectStore.Load(projectDirectory);

        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.UnsupportedSchemaVersion));
    }

    [Fact]
    public void 不认识的背景种类回退到纯色并报告()
    {
        ProjectLoadResult result = LoadWithBackground(new JsonObject
        {
            ["kind"] = "hologram",
            ["path"] = "/tmp/x.png",
        });

        Assert.Equal(BackgroundKind.Color, result.Project!.Background.Kind);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidBackgroundSource);
    }

    [Fact]
    public void 图片背景缺少路径时回退到纯色并报告()
    {
        ProjectLoadResult result = LoadWithBackground(new JsonObject { ["kind"] = "image" });

        Assert.Equal(BackgroundKind.Color, result.Project!.Background.Kind);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidBackgroundSource);
    }

    [Fact]
    public void 不认识的适配方式回退到完整显示并报告()
    {
        ProjectLoadResult result = LoadWithBackground(new JsonObject
        {
            ["kind"] = "image",
            ["path"] = "/tmp/x.png",
            ["fit"] = "stretch",
        });

        Assert.Equal(BackgroundKind.Image, result.Project!.Background.Kind);
        Assert.Equal(BackgroundFit.Contain, result.Project.Background.Fit);
        Assert.Contains(result.Report.Issues, issue => issue.Code == DiagnosticCode.InvalidBackgroundSource);
    }

    [Fact]
    public void 收集资源时把工程外的图片复制进工程包()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string source = WriteImage(Path.Combine(_directory.Path, "别处"), "bg.png");

        List<ProjectIssue> issues = [];
        Project collected = ProjectAssetCollector.Collect(
            Project.Create("工程", background: BackgroundSource.ForImage(source)),
            projectDirectory,
            issues);

        string expected = Path.Combine(projectDirectory, collected.Background.Media!.RelativePath);

        Assert.Equal(expected, collected.Background.Path);
        Assert.True(File.Exists(expected));
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(expected));
        Assert.Contains(issues, issue => issue.Code == DiagnosticCode.BackgroundAssetCopied);

        // 收进去之后再收一次不该再造一份：它已经在工程包里了。
        Assert.Same(collected, ProjectAssetCollector.Collect(collected, projectDirectory, []));
    }

    [Fact]
    public void 工程内旧位置的图片也固定到内容地址()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string imagePath = WriteImage(Path.Combine(projectDirectory, "assets"), "bg.png");

        Project project = Project.Create("工程", background: BackgroundSource.ForImage(imagePath));

        var collected = ProjectAssetCollector.Collect(project, projectDirectory, []);
        Assert.Equal(File.ReadAllBytes(imagePath), File.ReadAllBytes(collected.Background.Path!));
        Assert.EndsWith(".media", collected.Background.Path, StringComparison.Ordinal);
        Assert.Same(collected, ProjectAssetCollector.Collect(collected, projectDirectory, []));
    }

    [Fact]
    public void 源文件缺失拒绝收拢且保留源工程()
    {
        Project project = Project.Create(
            "工程",
            background: BackgroundSource.ForImage(Path.Combine(_directory.Path, "没有这个.png")));

        List<ProjectIssue> issues = [];
        Assert.Throws<FileNotFoundException>(() => ProjectAssetCollector.Collect(project, Path.Combine(_directory.Path, "作品"), issues));

        // 不静默把用户的引用丢掉：路径原样保留，只报告（docs §19）。
        Assert.EndsWith("没有这个.png", project.Background.Path, StringComparison.Ordinal);
        Assert.Null(project.Background.Media);
    }

    [Fact]
    public void 纯色背景不参与资源收集()
    {
        Project project = Project.Create("工程");

        Assert.Same(project, ProjectAssetCollector.Collect(project, _directory.Path, []));
    }

    [Fact]
    public void 同名不同内容的图片不覆盖已有文件()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string assets = Path.Combine(projectDirectory, ProjectAssetCollector.AssetsDirectoryName);
        string first = WriteImage(assets, "bg.png");

        // 另一张同名但内容不同的图（尺寸不同即内容不同）。
        string second = WriteImage(Path.Combine(_directory.Path, "别处"), "bg.png", size: 8);

        List<ProjectIssue> issues = [];
        Project collected = ProjectAssetCollector.Collect(
            Project.Create("工程", background: BackgroundSource.ForImage(second)),
            projectDirectory,
            issues);

        Assert.NotEqual(first, collected.Background.Path);
        Assert.True(File.Exists(collected.Background.Path));
        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(Path.Combine(assets, "bg.png")));
    }

    private ProjectLoadResult LoadWithBackground(JsonObject background)
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");

        bool sourceImage = background["kind"]?.GetValue<string>() == "image" && background.ContainsKey("path");
        ProjectStore.Save(sourceImage ? Project.Create("工程", background: BackgroundSource.ForImage(WriteImage(_directory.Path, "source.png"))) : Project.Create("工程"), projectDirectory);

        JsonObject document = JsonNode.Parse(
            File.ReadAllText(Path.Combine(projectDirectory, "project.json")))!.AsObject();
        if (sourceImage) background["path"] = document["background"]!["path"]!.DeepClone();
        else if (background["kind"]?.GetValue<string>() == "hologram") background.Remove("path");
        document["background"] = background;

        File.WriteAllText(
            Path.Combine(projectDirectory, "project.json"),
            document.ToJsonString());

        return ProjectStore.Load(projectDirectory);
    }

    private static string WriteImage(string directory, string fileName, int size = 16)
    {
        Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, fileName);

        // 内容用尺寸区分：同名不同内容时资源收集必须另起一个文件名。
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47, (byte)size]);

        return path;
    }
}
