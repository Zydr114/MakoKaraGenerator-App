using System.Reflection;
using MakoKaraGenerator.Application;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.Core.Persistence;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Media;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendTemplateTransferTests
{
    private static T Accepted<T>(BackendOperationResult<T> result) { Assert.True(result.Accepted, result.Error?.ToString()); return result.Response!; }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        internal void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal TemporaryDirectory Directory { get; } = new();
        internal Clock Time { get; } = new();
        internal ResourceStore Resources { get; }
        internal ProjectSessionRegistry Registry { get; }
        internal string Index => StyleTemplateLibrary.ResolvePath(Path.Combine(Directory.Path, "state", "libraries"));
        internal Fixture()
        {
            Resources = new(new() { ResourceRoot = Path.Combine(Directory.Path, "resources"), UnreferencedAssetLifetimeSeconds = 1 }, Time);
            Registry = new("host", Resources, new((_, _) => Task.FromResult<LoadedWaveform?>(null)), Time);
            Registry.ConfigurePersistence(new() { StateRoot = Path.Combine(Directory.Path, "state") }); Registry.ConfigureTemplateLibrary();
        }
        internal RequestMetadataDto Metadata() => new(Guid.NewGuid().ToString("N"), "host", Time.GetUtcNow().AddMinutes(1));
        internal async Task<TemplateImportPreflightResponse> Preflight(string owner = "client")
        {
            string path = Directory.FilePath(Guid.NewGuid().ToString("N") + ".mktpl");
            await StyleTemplateTransfer.WriteArchiveAsync(path, [StyleTemplate.Create("old", "传输", StyleTemplateKind.Line, LyricStyle.Default, [LyricStyleProperty.FontSizePx])]);
            var asset = await Resources.UploadAsync(owner, "test.mktpl", "application/octet-stream", new MemoryStream(await File.ReadAllBytesAsync(path)));
            return Accepted(await Registry.PreflightTemplateImportAsync(owner, "personal", new(asset.AssetId, Metadata())));
        }
        internal async Task<TemplateImportResponse> Import(TemplateImportPreflightResponse preflight, string revision = "missing", string owner = "client") =>
            Accepted(await Registry.ImportTemplatesAsync(owner, "personal", new(revision, preflight.PreflightId, Metadata())));
        public async ValueTask DisposeAsync() { await Registry.DisposeAsync(); Directory.Dispose(); }
    }

    [Fact]
    public async Task 临时归档使用私有目录且成功失败均清理自身文件()
    {
        await using Fixture f = new();
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var create = typeof(ProjectSessionRegistry).GetMethod("CreateTemplateTransferDirectory", flags)!;
        var prefix = (string)typeof(ProjectSessionRegistry).GetField("_templateTransferTempPrefix", flags)!.GetValue(f.Registry)!;
        var directory = (DirectoryInfo)create.Invoke(f.Registry, null)!;
        try
        {
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory.FullName));
        }
        finally { directory.Delete(recursive: true); }

        void AssertClean() => Assert.Empty(System.IO.Directory.EnumerateDirectories(Path.GetTempPath(), prefix + "*"));
        var preflight = await f.Preflight(); AssertClean();
        var imported = await f.Import(preflight);
        _ = Accepted(await f.Registry.ExportTemplatesAsync("client", "personal", new(imported.Revision, [imported.ImportedTemplates[0].TemplateId], f.Metadata())));
        AssertClean();
        var invalid = await f.Resources.UploadAsync("client", "invalid.mktpl", "application/octet-stream", new MemoryStream([1, 2, 3]));
        var rejected = await f.Registry.PreflightTemplateImportAsync("client", "personal", new(invalid.AssetId, f.Metadata()));
        Assert.Equal(BackendErrorCode.CommandRejected, rejected.Error?.Code);
        Assert.Equal("LibraryInvalid", rejected.Error?.FailureCode); AssertClean();
        Assert.False(f.Resources.HasClientReferences("client"));
    }

    [Fact]
    public async Task 预检固定完整内容不保留asset租约TTL配额和过期回收()
    {
        await using Fixture f = new();
        var first = await f.Preflight();
        Assert.False(f.Resources.HasClientReferences("client"));
        f.Time.Advance(2); f.Resources.CollectExpired();
        var imported = await f.Import(first); Assert.True(imported.Changed);
        _ = await f.Preflight(); _ = await f.Preflight();
        var builtins = Accepted(await f.Registry.ReadTemplatesAsync("builtin", null));
        var blocked = await f.Registry.ExportTemplatesAsync("client", "builtin", new(builtins.Revision, [builtins.Templates[0].TemplateId], f.Metadata()));
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, blocked.Error?.Code);
        f.Time.Advance(TemplateTransferLimits.LifetimeSeconds + 1);
        var export = Accepted(await f.Registry.ExportTemplatesAsync("client", "builtin", new(builtins.Revision, [builtins.Templates[0].TemplateId], f.Metadata())));
        Assert.Equal(builtins.Revision, export.Revision);
        Assert.Equal([builtins.Templates[0].TemplateId], export.TemplateIds);
        using (var content = Accepted(f.Registry.AcquireTemplateArchive("client", export.ExportId)))
        {
            using MemoryStream bytes = new(); await content.Stream.CopyToAsync(bytes);
            string path = f.Directory.FilePath("builtin.mktpl"); await File.WriteAllBytesAsync(path, bytes.ToArray());
            var archived = Assert.Single(await StyleTemplateTransfer.ReadArchiveAsync(path));
            var original = Assert.Single(StyleTemplateLibrary.BuiltIns, item => item.Id == export.TemplateIds[0]);
            Assert.Equal("exported:" + original.Id, archived.Id);
            Assert.Equal(StyleTemplateLibrary.Encode([original with { Id = archived.Id }]), StyleTemplateLibrary.Encode([archived]));
        }
        f.Time.Advance(TemplateTransferLimits.LifetimeSeconds + 1);
        Assert.Equal(BackendErrorCode.ResourceExpired, f.Registry.AcquireTemplateArchive("client", export.ExportId).Error?.Code);
        var expired = await f.Registry.ImportTemplatesAsync("client", "personal", new(imported.Revision, first.PreflightId, f.Metadata()));
        Assert.Equal(BackendErrorCode.ResourceNotFound, expired.Error?.Code);
    }

    [Fact]
    public async Task 原子库CAS失败保预检且接受后取消等待不阻止唯一导入()
    {
        await using Fixture f = new(); var preflight = await f.Preflight();
        var stale = await f.Registry.ImportTemplatesAsync("client", "personal", new(new string('0', 64), preflight.PreflightId, f.Metadata()));
        Assert.Equal(BackendErrorCode.RevisionConflict, stale.Error?.Code); Assert.False(File.Exists(f.Index));
        using var lease = LibraryCommitLease.TryEnter(f.Index);
        var request = new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata());
        using var cancellation = new CancellationTokenSource();
        var accepted = f.Registry.ImportTemplatesAsync("client", "personal", request, cancellation.Token);
        Assert.True(f.Registry.Receipts.HasUnexpiredReceipt("client", request.Metadata!.RequestId));
        cancellation.Cancel();
        Task shutdown = f.Registry.ShutdownAsync().AsTask(); Assert.False(shutdown.IsCompleted);
        lease.Dispose();
        try { Assert.True((await accepted.WaitAsync(TimeSpan.FromSeconds(10))).Accepted); }
        catch (OperationCanceledException) { /* accepted owner still commits once; only its waiter may cancel */ }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        var loaded = StyleTemplateLibrary.Load(f.Index); Assert.Single(loaded.Templates);
        Assert.NotEqual("old", loaded.Templates[0].Id);
    }

    [Fact]
    public async Task 调用前取消不占回执不提交预检仍可重试且异owner不可取得输入()
    {
        await using Fixture f = new(); var preflight = await f.Preflight();
        var request = new TemplateImportRequest("missing", preflight.PreflightId, f.Metadata());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Registry.ImportTemplatesAsync("client", "personal", request, cancellation.Token));
        Assert.False(f.Registry.Receipts.HasUnexpiredReceipt("client", request.Metadata!.RequestId)); Assert.False(File.Exists(f.Index));
        Assert.Equal(BackendErrorCode.ResourceNotFound, (await f.Registry.ImportTemplatesAsync("other", "personal", request)).Error?.Code);
        Assert.True(Accepted(await f.Registry.ImportTemplatesAsync("client", "personal", request)).Changed);
    }

    [Fact]
    public async Task 已取得归档读取TTL后仍计费保护owner且shutdown清目录不截断stream()
    {
        await using Fixture f = new(); var preflight = await f.Preflight(); var imported = await f.Import(preflight);
        var export = Accepted(await f.Registry.ExportTemplatesAsync("client", "personal", new(imported.Revision, [imported.ImportedTemplates[0].TemplateId], f.Metadata())));
        using var read = Accepted(f.Registry.AcquireTemplateArchive("client", export.ExportId));
        using MemoryStream expected = new(); await read.Stream.CopyToAsync(expected); read.Stream.Position = 0;
        f.Time.Advance(TemplateTransferLimits.LifetimeSeconds + 1);
        var has = typeof(ProjectSessionRegistry).GetMethod("HasTemplateTransfer", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.True((bool)has.Invoke(f.Registry, ["client"])!);
        Assert.Equal(BackendErrorCode.ResourceExpired, f.Registry.AcquireTemplateArchive("client", export.ExportId).Error?.Code);
        await f.Registry.ShutdownAsync();
        using MemoryStream after = new(); await read.Stream.CopyToAsync(after); Assert.Equal(expected.ToArray(), after.ToArray());
        Assert.Equal(BackendErrorCode.SessionClosing, f.Registry.AcquireTemplateArchive("client", export.ExportId).Error?.Code);
        read.Dispose(); read.Dispose();
    }
}
