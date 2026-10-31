using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>自动保存备份的位置策略与读写测试（roadmap `EDITOR-10`）。</summary>
public sealed class AutoSaveStoreTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 有工程目录时备份跟着工程走()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string stateRoot = Path.Combine(_directory.Path, "state");

        Assert.Equal(
            Path.Combine(projectDirectory, "backups"),
            AutoSaveStore.ResolveRoot(projectDirectory, stateRoot));
    }

    [Fact]
    public void 还没落盘的新工程备份到状态目录()
    {
        // 这一档不能省：否则"还没另存为"恰恰等于完全没有保护。
        string stateRoot = Path.Combine(_directory.Path, "state");

        Assert.Equal(
            Path.Combine(stateRoot, "autosave"),
            AutoSaveStore.ResolveRoot(null, stateRoot));
        Assert.Equal(
            Path.Combine(stateRoot, "autosave"),
            AutoSaveStore.ResolveRoot("   ", stateRoot));
    }

    [Fact]
    public void 写出的备份能被正常读回()
    {
        string root = Path.Combine(_directory.Path, "backups");

        string backupDirectory = AutoSaveStore.Save(BuildProject("备份"), root, Timestamp());

        ProjectLoadResult result = ProjectStore.Load(backupDirectory);

        Assert.NotNull(result.Project);
        Assert.Equal("备份", result.Project.Name);
        Assert.Equal(1920, result.Project.Composition.CanvasWidth);
        Assert.Equal(2, result.Project.Lyrics.Lines.Length);
    }

    [Fact]
    public void 同秒备份不覆写且保留最新独立快照()
    {
        string root = _directory.FilePath("backups");
        string first = AutoSaveStore.Save(BuildProject("一"), root, Timestamp(), retainCount: 2);
        byte[] original = File.ReadAllBytes(Path.Combine(first, ProjectJson.ProjectFileName));
        string second = AutoSaveStore.Save(BuildProject("二"), root, Timestamp(), retainCount: 2);

        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first, ProjectJson.ProjectFileName)));
        Assert.Equal([second, first], AutoSaveStore.List(root));
        Assert.Equal("二", ProjectStore.Load(second).Project!.Name);

        string third = AutoSaveStore.Save(BuildProject("三"), root, Timestamp(), retainCount: 2);
        Assert.False(Directory.Exists(first));
        Assert.Equal([third, second], AutoSaveStore.List(root));
        Assert.Equal("二", ProjectStore.Load(second).Project!.Name);
        Assert.Equal("三", ProjectStore.Load(third).Project!.Name);

        string fourth = AutoSaveStore.Save(BuildProject("四"), root, Timestamp(), retainCount: 2);
        Assert.False(Directory.Exists(second));
        Assert.Equal([fourth, third], AutoSaveStore.List(root));
        Assert.Equal("四", ProjectStore.Load(fourth).Project!.Name);
    }

    [Fact]
    public void 前置媒体已写入后分析失败不发布或裁剪备份()
    {
        string root = _directory.FilePath("backups");
        string first = AutoSaveStore.Save(BuildProject("旧备份"), root, Timestamp(), retainCount: 1);
        byte[] original = File.ReadAllBytes(Path.Combine(first, ProjectJson.ProjectFileName));
        string audio = _directory.FilePath("source.wav");
        File.WriteAllBytes(audio, [1, 2, 3, 4]);
        Project next = BuildProject("新备份") with
        {
            AudioPath = audio,
            AudioAnalysis = ProjectAudioAnalysis.Available(new(new string('0', 64), new string('a', 64), 1, [new(0, .1, .2, null, 0)])),
        };

        Assert.Throws<IOException>(() => AutoSaveStore.Save(next, root, Timestamp().AddSeconds(1), retainCount: 1));
        Assert.Equal([first], AutoSaveStore.List(root));
        Assert.Equal([first], Directory.GetDirectories(root));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(first, ProjectJson.ProjectFileName)));
        Assert.Equal("旧备份", ProjectStore.Load(first).Project!.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 同秒路径被文件或未完成目录占用时保持原内容(bool directory)
    {
        string root = _directory.FilePath("backups");
        Directory.CreateDirectory(root);
        string occupied = Path.Combine(root, AutoSaveStore.BackupDirectoryName(Timestamp()));
        string original = occupied;
        if (directory)
        {
            Directory.CreateDirectory(occupied);
            original = Path.Combine(occupied, "sentinel");
        }
        File.WriteAllText(original, "保留文件");

        string backup = AutoSaveStore.Save(BuildProject("备份"), root, Timestamp(), retainCount: 1);
        Assert.NotEqual(occupied, backup);
        Assert.Equal("保留文件", File.ReadAllText(original));
        Assert.Equal([backup], AutoSaveStore.List(root));
        Assert.Equal("备份", ProjectStore.Load(backup).Project!.Name);
    }

    [Fact]
    public async Task 同秒并发发布不混合工程快照()
    {
        string root = _directory.FilePath("backups");
        string[] names = ["一", "二", "三", "四", "五", "六"];
        string[] backups = await Task.WhenAll(names.Select(name => Task.Run(() =>
            AutoSaveStore.Save(BuildProject(name), root, Timestamp()))));

        Assert.Equal(names.Length, backups.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(names.Length, AutoSaveStore.List(root).Count);
        for (int i = 0; i < names.Length; i++)
            Assert.Equal(names[i], ProjectStore.Load(backups[i]).Project!.Name);
        Assert.Empty(Directory.GetDirectories(root, ".autosave-*.tmp"));
    }

    [Fact]
    public void 时钟回拨后成功保存仍保留本次恢复现场()
    {
        string root = _directory.FilePath("backups");
        string previous = AutoSaveStore.Save(BuildProject("上次备份"), root, Timestamp(), retainCount: 1);
        string latest = AutoSaveStore.Save(BuildProject("本次备份"), root, Timestamp().AddSeconds(-1), retainCount: 1);

        Assert.False(Directory.Exists(previous));
        Assert.Equal([latest], AutoSaveStore.List(root));
        Assert.Equal("本次备份", ProjectStore.Load(latest).Project!.Name);
    }

    [Fact]
    public void 只保留指定份数并删掉最旧的()
    {
        string root = Path.Combine(_directory.Path, "backups");
        DateTimeOffset start = Timestamp();

        string first = AutoSaveStore.Save(BuildProject("一"), root, start, retainCount: 2);
        string second = AutoSaveStore.Save(BuildProject("二"), root, start.AddSeconds(1), retainCount: 2);
        string third = AutoSaveStore.Save(BuildProject("三"), root, start.AddSeconds(2), retainCount: 2);

        Assert.False(Directory.Exists(first), "最旧的一份应当被剪掉。");
        Assert.True(Directory.Exists(second));
        Assert.True(Directory.Exists(third));

        Assert.Equal([third, second], AutoSaveStore.List(root));
    }

    [Fact]
    public void 备份比工程新才算有现场()
    {
        string projectDirectory = Path.Combine(_directory.Path, "作品");
        string root = Path.Combine(projectDirectory, "backups");

        ProjectStore.Save(BuildProject("盘上的"), projectDirectory);
        string backupDirectory = AutoSaveStore.Save(BuildProject("备份"), root, Timestamp());

        // 备份是后写的：算有现场。
        Assert.True(AutoSaveStore.IsNewerThanProject(backupDirectory, projectDirectory));

        // 把工程文件的时间往后拨：现在盘上的更新，不算现场。
        File.SetLastWriteTimeUtc(
            Path.Combine(projectDirectory, ProjectJson.ProjectFileName),
            DateTime.UtcNow.AddMinutes(5));

        Assert.False(AutoSaveStore.IsNewerThanProject(backupDirectory, projectDirectory));
    }

    [Fact]
    public void 工程文件不存在时备份也算现场()
    {
        string projectDirectory = Path.Combine(_directory.Path, "还没写过");
        Directory.CreateDirectory(projectDirectory);

        string backupDirectory = AutoSaveStore.Save(
            BuildProject("备份"),
            Path.Combine(projectDirectory, "backups"),
            Timestamp());

        Assert.True(AutoSaveStore.IsNewerThanProject(backupDirectory, projectDirectory));
    }

    [Fact]
    public void 备份目录不存在时列表为空()
    {
        Assert.Empty(AutoSaveStore.List(Path.Combine(_directory.Path, "没有这个目录")));

        // 剪枝也不该因为"目录不存在"而报错：它每次保存后都会跑。
        AutoSaveStore.Prune(Path.Combine(_directory.Path, "没有这个目录"), 3);
    }

    private static DateTimeOffset Timestamp() => new(2026, 9, 25, 18, 40, 12, TimeSpan.Zero);

    private static Project BuildProject(string name) => Project.Create(
        name,
        Composition.Create(1920, 1080, FrameRate.Fps30, outputDurationUs: 2_000_000),
        LyricsDocument.Create(
        [
            LyricLine.Create("l1", "夜", new TimeRange(0, 1_000_000), TimingStatus.Edited),
            LyricLine.Create("l2", "空", new TimeRange(1_000_000, 2_000_000), TimingStatus.Edited),
        ]));
}
