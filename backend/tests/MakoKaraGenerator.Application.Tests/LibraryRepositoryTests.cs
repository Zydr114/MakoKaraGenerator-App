using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed partial class LibraryRepositoryTests
{
    internal static StyleTemplate Template(string id, string name = "模板") => StyleTemplate.Create(id, name,
        StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.FontSizePx]);
    [Fact]
    public void 缺失空库损坏库与实际字节修订分别处理()
    {
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        var missing = repo.Read(); Assert.Empty(missing.Issues); Assert.Equal(LibraryIndexRevision.Missing, missing.Revision);
        var empty = repo.Save(missing.Revision, []);
        Assert.Empty(empty.Templates); Assert.NotEqual(missing.Revision, empty.Revision);
        Assert.Equal(empty.Revision, repo.Read().Revision); Assert.Equal(LibraryIndexRevision.Read(repo.Path, StyleTemplateLibrary.MaximumIndexBytes), empty.Revision);
        File.AppendAllText(repo.Path, "\n"); Assert.NotEqual(empty.Revision, repo.Read().Revision);
        Assert.Throws<LibraryRevisionConflictException>(() => repo.Save(empty.Revision, [Template("lost")]));
        File.WriteAllText(repo.Path, "broken json"); var broken = repo.Read();
        Assert.NotEmpty(broken.Issues); Assert.NotEqual(LibraryIndexRevision.Missing, broken.Revision);
        Assert.Throws<IOException>(() => repo.Save(broken.Revision, [])); Assert.Equal("broken json", File.ReadAllText(repo.Path));
        byte[] invalidUtf8 = [0xff, 0xfe, 0x80]; File.WriteAllBytes(repo.Path, invalidUtf8);
        var invalid = repo.Read(); Assert.NotEmpty(invalid.Issues); Assert.Equal(LibraryIndexRevision.FromBytes(invalidUtf8), invalid.Revision);
        string scriptPath = ScriptConfigurationLibrary.ResolvePath(temp.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!); File.WriteAllBytes(scriptPath, invalidUtf8);
        var script = new ScriptConfigurationRepository(scriptPath).Read();
        Assert.NotEmpty(script.Issues); Assert.Equal(invalid.Revision, script.Revision);
    }
    [Fact]
    public void 保存返回自己的固定快照通知只一次且迟到取消与观察者异常不改成功()
    {
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        var initial = repo.Read(); using var cancel = new CancellationTokenSource(); int notices = 0;
        StyleTemplateLibraryLoadResult? noticed = null;
        repo.Changed += snapshot => { notices++; noticed = snapshot; cancel.Cancel(); throw new InvalidOperationException("观察者失败"); };
        var saved = repo.Save(initial.Revision, [Template("first")], cancel.Token);
        Assert.True(cancel.IsCancellationRequested); Assert.Equal(1, notices); Assert.Equal(saved.Revision, repo.Read().Revision);
        Assert.Same(saved, noticed); Assert.Single(Assert.IsType<StyleTemplateLibraryLoadResult>(noticed).Templates);
        Assert.Throws<LibraryRevisionConflictException>(() => repo.Save(initial.Revision, [Template("second")]));
        Assert.Equal(1, notices); Assert.Equal("first", Assert.Single(repo.Read().Templates).Id);
    }
    [Fact]
    public void 资源准备后的最后修订检查拒绝覆盖外部新入口()
    {
        using TemporaryDirectory temp = new(); string path = StyleTemplateLibrary.ResolvePath(temp.Path);
        StyleTemplateLibrary.Save(path, [Template("original")]); var expected = StyleTemplateLibrary.Load(path).Revision;
        File.AppendAllText(path, "\n"); byte[] external = File.ReadAllBytes(path);
        var package = MixedEffectPackageFixture.MaterialContents();
        var candidate = StyleTemplate.Create("candidate", "资源", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } },
            [LyricStyleProperty.UnsungFill], packages: [package]);
        using var lease = LibraryCommitLease.TryEnter(path);
        Assert.Throws<LibraryRevisionConflictException>(() => StyleTemplateLibrary.Save(path, [candidate], expected));
        Assert.Equal(external, File.ReadAllBytes(path));
        Assert.True(Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, StyleTemplateLibrary.PackagePath(package.Revision))));
        Assert.Equal("original", Assert.Single(StyleTemplateLibrary.Load(path).Templates).Id);
    }
    [Fact]
    public void 发布后外部编辑不能被混入本次返回修订或下一次旧快照提交()
    {
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        repo.Changed += _ => File.AppendAllText(repo.Path, "\n");
        var saved = repo.Save(repo.Read().Revision, [Template("own")]);
        Assert.NotEqual(saved.Revision, repo.Read().Revision);
        Assert.Throws<LibraryRevisionConflictException>(() => repo.Save(saved.Revision, [Template("later")]));
        Assert.Equal("own", Assert.Single(repo.Read().Templates).Id);
    }
    [Fact]
    public async Task 进程内不同仓储也参加同一互斥锁且等待可取消()
    {
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        var initial = repo.Read(); using (var lease = LibraryCommitLease.TryEnter(repo.Path))
        {
            Assert.Throws<LibraryBusyException>(() => repo.Save(initial.Revision, [Template("busy")]));
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repo.SaveAsync(initial.Revision, [Template("cancel")], cancel.Token));
            Assert.False(File.Exists(repo.Path));
        }
        var saved = await repo.SaveAsync(initial.Revision, [Template("after")]); Assert.Equal(saved.Revision, repo.Read().Revision);
        Assert.True(File.Exists(LibraryCommitLease.LockPath(repo.Path)));
    }
    [Fact]
    public void 不可变资源写失败保持旧入口且不发成功通知()
    {
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        var first = repo.Save(repo.Read().Revision, [Template("old")]); byte[] bytes = File.ReadAllBytes(repo.Path); int notices = 0;
        repo.Changed += _ => notices++;
        var package = MixedEffectPackageFixture.MaterialContents();
        string blocked = Path.Combine(Path.GetDirectoryName(repo.Path)!, StyleTemplateLibrary.PackagePath(package.Revision));
        Directory.CreateDirectory(Path.GetDirectoryName(blocked)!); File.WriteAllText(blocked, "blocked resource location");
        var candidate = StyleTemplate.Create("candidate", "资源", StyleTemplateKind.Line,
            LyricStyle.Default with { UnsungFill = new() { Material = new MaterialSpec.PackageMaterial(package.PackageId, package.Revision, "ink") } },
            [LyricStyleProperty.UnsungFill], packages: [package]);
        Assert.ThrowsAny<IOException>(() => repo.Save(first.Revision, [.. first.Templates, candidate]));
        Assert.Equal(bytes, File.ReadAllBytes(repo.Path)); Assert.Equal(0, notices);
    }
    [Fact]
    public void 真实索引发布失败保持原字节且不通知成功()
    {
        if (!OperatingSystem.IsLinux()) return; // This regression asserts an actual Linux permission failure.
        using TemporaryDirectory temp = new(); var repo = new TemplateLibraryRepository(StyleTemplateLibrary.ResolvePath(temp.Path));
        var first = repo.Save(repo.Read().Revision, [Template("old")]); byte[] bytes = File.ReadAllBytes(repo.Path); int notices = 0;
        repo.Changed += _ => notices++;
        string folder = Path.GetDirectoryName(repo.Path)!; var mode = File.GetUnixFileMode(folder);
        try
        {
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.ThrowsAny<UnauthorizedAccessException>(() => repo.Save(first.Revision, [Template("new")]));
            Assert.Equal(bytes, File.ReadAllBytes(repo.Path)); Assert.Equal(0, notices);
        }
        finally { File.SetUnixFileMode(folder, mode); }
    }
}
