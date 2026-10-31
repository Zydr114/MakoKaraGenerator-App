using MakoKaraGenerator.Core.Persistence;

namespace MakoKaraGenerator.Core.Tests.Persistence;

/// <summary>"上次会话"标记的测试（自动保存恢复靠它找到该去哪找备份）。</summary>
public sealed class AutoSaveSessionTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    /// <inheritdoc/>
    public void Dispose() => _directory.Dispose();

    [Fact]
    public void 写入读回一致()
    {
        AutoSaveSession session = new(
            Path.Combine(_directory.Path, "作品", "backups", "autosave-20260925-184012Z"),
            Path.Combine(_directory.Path, "作品"),
            "夜空中最亮的星",
            new DateTimeOffset(2026, 9, 25, 18, 40, 12, TimeSpan.Zero));

        AutoSaveSession.Write(_directory.Path, session);

        Assert.Equal(session, AutoSaveSession.Read(_directory.Path));
    }

    [Fact]
    public void 新工程的标记里没有工程目录()
    {
        AutoSaveSession session = new(
            Path.Combine(_directory.Path, "autosave", "autosave-20260925-184012Z"),
            ProjectDirectoryPath: null,
            "未命名",
            new DateTimeOffset(2026, 9, 25, 18, 40, 12, TimeSpan.Zero));

        AutoSaveSession.Write(_directory.Path, session);

        AutoSaveSession? read = AutoSaveSession.Read(_directory.Path);

        Assert.NotNull(read);
        Assert.Null(read.ProjectDirectoryPath);
    }

    [Fact]
    public void 没有标记时为空()
    {
        Assert.Null(AutoSaveSession.Read(_directory.Path));
    }

    [Fact]
    public void 标记损坏时当作没有()
    {
        // 这个文件存在的唯一目的是帮用户找回工作，它自己出问题绝不该拦住启动。
        File.WriteAllText(AutoSaveSession.PathFor(_directory.Path), "{ 这不是 JSON");

        Assert.Null(AutoSaveSession.Read(_directory.Path));
    }

    [Fact]
    public void 清除标记()
    {
        AutoSaveSession.Write(
            _directory.Path,
            new AutoSaveSession("某处", null, "未命名", DateTimeOffset.UtcNow));

        AutoSaveSession.Clear(_directory.Path);

        Assert.Null(AutoSaveSession.Read(_directory.Path));

        // 本来就没有时清除也不该报错：用户关掉恢复对话框时可能已经清过。
        AutoSaveSession.Clear(_directory.Path);
    }
}
