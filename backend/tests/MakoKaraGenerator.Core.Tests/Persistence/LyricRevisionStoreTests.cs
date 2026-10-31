using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MakoKaraGenerator.Core.Diagnostics;
using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Persistence;

public sealed class LyricRevisionStoreTests
{
    [Fact]
    public void 实际UTF8字节决定内容地址且没有第二份歌词真值()
    {
        using TemporaryDirectory directory = new();
        Project project = Source();
        ProjectStore.Save(project, directory.Path);
        JsonNode entry = Entry(directory.Path);
        byte[] bytes = File.ReadAllBytes(ProjectFileFixture.LyricsPath(directory.Path));
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Assert.Equal("data/lyrics/" + hash + ".json", entry["lyricsFile"]!.GetValue<string>());
        Assert.Equal(Project.CurrentSchemaVersion, entry["schemaVersion"]!.GetValue<int>());
        Assert.Equal(ProductVersion.Current, entry["applicationVersion"]!.GetValue<string>());
        Assert.Contains("你好", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal(LyricsDocument.CurrentSchemaVersion, JsonNode.Parse(bytes)!["schemaVersion"]!.GetValue<int>());
        Assert.False(File.Exists(directory.FilePath("lyrics.json")));
        Assert.Equal(project, ProjectStore.Load(directory.Path).Project);
    }

    [Fact]
    public void 同歌词修订复用且只改工程字段不重写歌词()
    {
        using TemporaryDirectory directory = new();
        Project project = Source();
        ProjectStore.Save(project, directory.Path);
        string firstPath = ProjectFileFixture.LyricsPath(directory.Path);
        byte[] firstEntry = File.ReadAllBytes(directory.FilePath("project.json"));
        byte[] first = File.ReadAllBytes(firstPath);
        DateTime stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(firstPath, stamp);
        ProjectStore.Save(project, directory.Path);
        Assert.Equal(firstEntry, File.ReadAllBytes(directory.FilePath("project.json")));
        ProjectStore.Save(project with { Name = "只改入口" }, directory.Path);
        Assert.Equal(firstPath, ProjectFileFixture.LyricsPath(directory.Path));
        Assert.Equal(first, File.ReadAllBytes(firstPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(firstPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(firstPath)!));
        Assert.Equal("只改入口", ProjectStore.Load(directory.Path).Project!.Name);
    }

    [Fact]
    public void 改歌词发布新修订而旧入口仍能还原旧数据()
    {
        using TemporaryDirectory directory = new();
        Project original = Source();
        ProjectStore.Save(original, directory.Path);
        string entryPath = directory.FilePath("project.json");
        byte[] oldEntry = File.ReadAllBytes(entryPath);
        string oldPath = ProjectFileFixture.LyricsPath(directory.Path);
        byte[] oldLyrics = File.ReadAllBytes(oldPath);
        Project edited = Source("新词");
        ProjectStore.Save(edited, directory.Path);
        Assert.NotEqual(oldPath, ProjectFileFixture.LyricsPath(directory.Path));
        Assert.Equal(oldLyrics, File.ReadAllBytes(oldPath));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(oldPath)!).Length);
        Assert.Equal(edited, ProjectStore.Load(directory.Path).Project);
        File.WriteAllBytes(entryPath, oldEntry);
        Assert.Equal(original, ProjectStore.Load(directory.Path).Project);
    }

    [Fact]
    public void 已存在内容地址损坏时保存拒绝覆写且不发布新入口()
    {
        using TemporaryDirectory directory = new();
        Project project = Source();
        ProjectStore.Save(project, directory.Path);
        string path = ProjectFileFixture.LyricsPath(directory.Path);
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json"));
        File.WriteAllText(path, "被篡改的历史修订");
        Assert.Throws<IOException>(() => ProjectStore.Save(project with { Name = "不应发布" }, directory.Path));
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json")));
        Assert.Equal("被篡改的历史修订", File.ReadAllText(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Linux入口发布失败保留旧可读工程和未引用新修订()
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using TemporaryDirectory directory = new();
        Project original = Source();
        ProjectStore.Save(original, directory.Path);
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json"));
        string oldPath = ProjectFileFixture.LyricsPath(directory.Path);
        byte[] oldLyrics = File.ReadAllBytes(oldPath);
        UnixFileMode mode = File.GetUnixFileMode(directory.Path);
        try
        {
            // 歌词子目录仍可写；只有最后的入口临时文件／替换被禁止。
            File.SetUnixFileMode(directory.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Throws<UnauthorizedAccessException>(() => ProjectStore.Save(Source("新词"), directory.Path));
            Assert.Equal(original, ProjectStore.Load(directory.Path).Project);
        }
        finally { File.SetUnixFileMode(directory.Path, mode); }
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json")));
        Assert.Equal(oldLyrics, File.ReadAllBytes(oldPath));
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(oldPath)!).Length);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lyrics.json")]
    [InlineData("data/lyrics/invalid.json")]
    [InlineData("../data/lyrics/hash.json")]
    [InlineData("/tmp/lyrics.json")]
    [InlineData("data\\lyrics\\invalid.json")]
    [InlineData("Data/lyrics/0000000000000000000000000000000000000000000000000000000000000000.json")]
    [InlineData("data/lyrics/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.json")]
    [InlineData("data/lyrics/../lyrics/0000000000000000000000000000000000000000000000000000000000000000.json")]
    [InlineData("data/lyrics/0000000000000000000000000000000000000000000000000000000000000000.JSON")]
    public void 非规范引用不能生成可保存的空工程(string? reference)
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Source(), directory.Path);
        JsonNode entry = Entry(directory.Path);
        entry["lyricsFile"] = reference;
        File.WriteAllText(directory.FilePath("project.json"), entry.ToJsonString());
        byte[] before = File.ReadAllBytes(directory.FilePath("project.json"));
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project);
        Assert.False(result.CanExport);
        Assert.True(result.Report.Contains(DiagnosticCode.InvalidLyricsReference), result.Report.ToSummary());
        Assert.Equal(before, File.ReadAllBytes(directory.FilePath("project.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 缺失或被改字节的修订拒绝加载(bool missing)
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Source(), directory.Path);
        string path = ProjectFileFixture.LyricsPath(directory.Path);
        if (missing) { File.Delete(path); }
        else { File.AppendAllText(path, " "); }
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(missing ? DiagnosticCode.MissingLyricsFile : DiagnosticCode.LyricsContentMismatch));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":999}")]
    [InlineData("{}")]
    public void 内容hash合法但JSON或schema非法仍拒绝加载(string json)
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Source(), directory.Path);
        string reference = ProjectFileFixture.PublishLyrics(directory.Path, json);
        byte[] before = File.ReadAllBytes(directory.FilePath(reference));
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project);
        Assert.True(result.Report.HasErrors);
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Code == DiagnosticCode.LyricsContentMismatch);
        Assert.Equal(before, File.ReadAllBytes(directory.FilePath(reference)));
    }

    [Fact]
    public void 版本门禁先于歌词引用和内容检查()
    {
        using TemporaryDirectory directory = new();
        ProjectStore.Save(Source(), directory.Path);
        File.Delete(ProjectFileFixture.LyricsPath(directory.Path));
        JsonNode entry = Entry(directory.Path);
        entry["applicationVersion"] = "0.13.3";
        entry.AsObject().Remove("lyricsFile");
        File.WriteAllText(directory.FilePath("project.json"), entry.ToJsonString());
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project);
        Assert.Equal(DiagnosticCode.ApplicationVersionMismatch, Assert.Single(result.Report.Issues).Code);
    }

    [Fact]
    public void 根目录旧歌词不被读取删除或覆写()
    {
        using TemporaryDirectory directory = new();
        directory.WriteFile("lyrics.json", "保留旧文件");
        ProjectStore.Save(Source(), directory.Path);
        Assert.Equal(Source(), ProjectStore.Load(directory.Path).Project);
        Assert.Equal("保留旧文件", directory.ReadFile("lyrics.json"));
    }

    [Fact]
    public void UTF8标记注释和尾随逗号保持可读且hash仍校验实际字节()
    {
        using TemporaryDirectory directory = new();
        Project original = Source();
        ProjectStore.Save(original, directory.Path);
        string json = ProjectFileFixture.ReadLyrics(directory.Path);
        int closing = json.LastIndexOf('}');
        json = json.Insert(closing, ",");
        ProjectFileFixture.PublishLyrics(directory.Path, "\uFEFF//编码与可读注释\n" + json);
        string entry = directory.ReadFile("project.json");
        File.WriteAllText(directory.FilePath("project.json"), "//入口注释\n" + entry, new UTF8Encoding(true));
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Equal(original, result.Project);
        Assert.DoesNotContain(result.Report.Issues, issue => issue.Code is DiagnosticCode.MalformedJson or DiagnosticCode.LyricsContentMismatch);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("data/lyrics")]
    [InlineData("file")]
    public void Linux修订目录或文件链接拒绝读写且保留外部数据(string scope)
    {
        if (!OperatingSystem.IsLinux()) { return; }
        using TemporaryDirectory directory = new();
        using TemporaryDirectory external = new();
        ProjectStore.Save(Source(), directory.Path);
        string revisionPath = ProjectFileFixture.LyricsPath(directory.Path);
        byte[] entry = File.ReadAllBytes(directory.FilePath("project.json"));
        if (scope == "file")
        {
            string externalPath = external.FilePath("revision.json");
            File.Move(revisionPath, externalPath);
            File.CreateSymbolicLink(revisionPath, externalPath);
        }
        else
        {
            string path = directory.FilePath(scope);
            string destination = external.FilePath("copied");
            Directory.Move(path, destination);
            Directory.CreateSymbolicLink(path, destination);
        }
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.Null(result.Project);
        Assert.True(result.Report.Contains(DiagnosticCode.InvalidLyricsReference));
        string externalRevision = Assert.Single(Directory.GetFiles(external.Path, "*.json", SearchOption.AllDirectories));
        byte[] bytes = File.ReadAllBytes(externalRevision);
        Assert.Throws<IOException>(() => ProjectStore.Save(Source(), directory.Path));
        Assert.Equal(bytes, File.ReadAllBytes(externalRevision));
        Assert.Equal(entry, File.ReadAllBytes(directory.FilePath("project.json")));
    }

    [Fact]
    public async Task 并发保存只发布完整入口且每份歌词均保留()
    {
        using TemporaryDirectory directory = new();
        Project[] projects = Enumerable.Range(0, 8).Select(index => Source("歌词" + index)).ToArray();
        await Task.WhenAll(projects.Select(project => Task.Run(() => ProjectStore.Save(project, directory.Path))));
        ProjectLoadResult result = ProjectStore.Load(directory.Path);
        Assert.True(result.Loaded, result.Report.ToSummary());
        Assert.Contains(result.Project, projects);
        string[] revisions = Directory.GetFiles(directory.FilePath(ProjectJson.LyricsDirectoryName));
        Assert.Equal(8, revisions.Length);
        foreach (string path in revisions)
            Assert.Equal(Path.GetFileNameWithoutExtension(path), Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp", SearchOption.AllDirectories));
    }

    private static JsonNode Entry(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "project.json")))!;

    private static Project Source(string text = "你好") => Project.Create("内容地址", lyrics: LyricsDocument.Create([
        LyricLine.Create("line", text, new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed,
            [LyricToken.Create("token", text, new TimeRange(1_000_000, 2_000_000), TimingStatus.Confirmed)]),
    ]));
}
