using System.Text;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Application.Tests;

public sealed class TemplateLibraryCatalogueTests
{
    [Fact]
    public async Task 仅激活读取普通快照访问和隐藏刷新不读盘()
    {
        var source = new Source(); await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        Assert.Equal(0, source.Reads); Assert.False(catalogue.Snapshot.CanApply);
        using (catalogue.Activate())
        {
            await catalogue.WaitForIdleAsync(); Assert.True(catalogue.Snapshot.CanApply);
            Assert.Equal(1, source.Reads); int revisions = source.Revisions;
            for (int index = 0; index < 100; index++) Assert.Single(catalogue.Snapshot.Library.Templates);
            Assert.Equal(1, source.Reads); Assert.Equal(revisions, source.Revisions);
        }
        catalogue.Refresh(); await catalogue.WaitForIdleAsync(); Assert.Equal(1, source.Reads);
        using (catalogue.Activate()) { await catalogue.WaitForIdleAsync(); Assert.Equal(2, source.Reads); }
        using (catalogue.Activate()) { await catalogue.WaitForIdleAsync(); Assert.Equal(2, source.Reads); }
    }

    [Fact]
    public async Task 重复失效合并且单个后台读取器保持所有权()
    {
        var source = new Source(); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.Reader = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return source.Value; };
        await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            for (int index = 0; index < 100; index++) catalogue.Refresh();
            Assert.False(catalogue.Snapshot.CanApply); Assert.Equal(1, source.Reads);
        }
        finally { release.Set(); }
        await catalogue.WaitForIdleAsync(); Assert.Equal(2, source.Reads); Assert.Equal(1, source.MaximumReaders);
        Assert.True(catalogue.Snapshot.CanApply);
    }

    [Fact]
    public async Task 本进程新发布立即采用固定修订且迟到旧读取不能覆盖()
    {
        var source = new Source(); var old = source.Value;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.Reader = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return old; };
        await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            source.Publish(Snapshot("new")); Assert.Equal("new", Assert.Single(catalogue.Snapshot.Library.Templates).Id);
            Assert.False(catalogue.Snapshot.CanApply);
        }
        finally { release.Set(); }
        await catalogue.WaitForIdleAsync(); Assert.Equal("new", Assert.Single(catalogue.Snapshot.Library.Templates).Id); Assert.Equal(1, source.Reads);
        Assert.True(catalogue.Snapshot.CanApply);
    }

    [Fact]
    public async Task 较晚旧提交通知须复核实际字节后才可应用()
    {
        var source = new Source(); var old = source.Value;
        await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); await catalogue.WaitForIdleAsync();
        source.Publish(Snapshot("current")); await catalogue.WaitForIdleAsync();
        Assert.Equal("current", Assert.Single(catalogue.Snapshot.Library.Templates).Id);
        source.Notice(old); await catalogue.WaitForIdleAsync();
        Assert.True(catalogue.Snapshot.CanApply); Assert.Equal("current", Assert.Single(catalogue.Snapshot.Library.Templates).Id);
    }

    [Fact]
    public async Task 加载中离开丢弃迟到结果且隐藏不重试再激活读取最新()
    {
        var source = new Source(); var old = source.Value;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.Reader = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return old; };
        await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        var active = catalogue.Activate(); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        active.Dispose(); source.Value = Snapshot("next"); catalogue.Refresh(); release.Set();
        await catalogue.WaitForIdleAsync(); Assert.False(catalogue.Snapshot.IsLoaded); Assert.Equal(1, source.Reads);
        source.Reader = null;
        using (catalogue.Activate()) { await catalogue.WaitForIdleAsync(); Assert.Equal("next", Assert.Single(catalogue.Snapshot.Library.Templates).Id); }
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task 失败保留最后成功内容过期门禁及成功刷新恢复()
    {
        var source = new Source(); await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); await catalogue.WaitForIdleAsync();
        source.Reader = () => throw new IOException("read failed"); catalogue.Refresh(); await catalogue.WaitForIdleAsync();
        Assert.Equal("old", Assert.Single(catalogue.Snapshot.Library.Templates).Id); Assert.True(catalogue.Snapshot.IsStale);
        Assert.Equal("read failed", catalogue.Snapshot.Error); Assert.False(catalogue.Snapshot.CanApply);
        source.Reader = () => new([], ["闭包损坏"]) { Revision = source.Value.Revision };
        catalogue.Refresh(); await catalogue.WaitForIdleAsync(); Assert.Equal("闭包损坏", catalogue.Snapshot.Error);
        Assert.Equal("old", Assert.Single(catalogue.Snapshot.Library.Templates).Id);
        source.Reader = null; source.Value = Snapshot("restored"); catalogue.Refresh(); await catalogue.WaitForIdleAsync();
        Assert.Null(catalogue.Snapshot.Error); Assert.True(catalogue.Snapshot.CanApply);
        Assert.Equal("restored", Assert.Single(catalogue.Snapshot.Library.Templates).Id);
    }

    [Fact]
    public async Task 读期间索引改变明确拒绝发布而不是采用部分旧快照()
    {
        var source = new Source(); var old = source.Value;
        source.Reader = () => { source.Value = Snapshot("external"); return old; };
        await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); await catalogue.WaitForIdleAsync();
        Assert.False(catalogue.Snapshot.CanApply); Assert.Contains("读取期间改变", catalogue.Snapshot.Error);
        Assert.Empty(catalogue.Snapshot.Library.Templates);
        source.Reader = null; catalogue.Refresh(); await catalogue.WaitForIdleAsync(); Assert.True(catalogue.Snapshot.CanApply);
    }

    [Fact]
    public async Task 多租约和观察者异常不串队列已释放租约不可重复减计数()
    {
        var source = new Source(); await using var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        catalogue.Changed += () => throw new InvalidOperationException("observer");
        var first = catalogue.Activate(); using var second = catalogue.Activate(); await catalogue.WaitForIdleAsync();
        first.Dispose(); first.Dispose(); catalogue.Refresh(); await catalogue.WaitForIdleAsync();
        Assert.True(catalogue.Snapshot.CanApply); Assert.Equal(2, source.Reads);
        second.Dispose(); catalogue.Refresh(); Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task 目录释放等待自身后台工作并拒绝迟到发布()
    {
        var source = new Source(); using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        source.Reader = () => { started.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); return source.Value; };
        var catalogue = new TemplateLibraryCatalogue(source, watchFiles: false);
        using var active = catalogue.Activate(); Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        var stopping = catalogue.DisposeAsync().AsTask(); Assert.False(stopping.IsCompleted);
        release.Set(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        source.Publish(Snapshot("late")); Assert.False(catalogue.Snapshot.IsLoaded);
        Assert.Throws<ObjectDisposedException>(() => catalogue.Activate());
    }

    private static StyleTemplateLibraryLoadResult Snapshot(string id) => new([LibraryRepositoryTests.Template(id)], [])
    { Revision = LibraryIndexRevision.FromBytes(Encoding.UTF8.GetBytes(id)) };
    private sealed class Source : ITemplateLibrarySource
    {
        public string Path => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "catalogue-test", "index.json");
        public StyleTemplateLibraryLoadResult Value { get; set; } = Snapshot("old");
        public Func<StyleTemplateLibraryLoadResult>? Reader { get; set; }
        public int Reads, Revisions, MaximumReaders;
        private int _readers;
        public event Action<StyleTemplateLibraryLoadResult>? Changed;
        public StyleTemplateLibraryLoadResult Read()
        {
            Interlocked.Increment(ref Reads); int readers = Interlocked.Increment(ref _readers);
            MaximumReaders = Math.Max(MaximumReaders, readers);
            try { return Reader?.Invoke() ?? Value; } finally { Interlocked.Decrement(ref _readers); }
        }
        public LibraryIndexRevision ReadRevision() { Interlocked.Increment(ref Revisions); return Value.Revision; }
        public void Publish(StyleTemplateLibraryLoadResult value) { Value = value; Changed?.Invoke(value); }
        public void Notice(StyleTemplateLibraryLoadResult value) => Changed?.Invoke(value);
    }
}
