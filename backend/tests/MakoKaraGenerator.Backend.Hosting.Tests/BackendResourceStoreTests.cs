using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendResourceStoreTests
{
    [Fact]
    public async Task 未发布候选释放文件字节与数量且不可跨客户端接受()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 4, MaxAssetQuotaBytes = 4, MaxAssets = 1 });
        using ProjectSessionRegistry registry = new("host", store, new((_, _) => Task.FromResult<MakoKaraGenerator.Media.LoadedWaveform?>(null)));
        using (ResourceStore.PreparedUpload candidate = await store.PrepareUploadAsync("client", "a.wav", "audio/wav", new MemoryStream([1, 2, 3, 4])))
        {
            Assert.Equal(4, store.UsedBytes);
            Assert.Single(Directory.GetFiles(directory.Path, "*.upload", SearchOption.AllDirectories));
            ResourceStoreException full = await Assert.ThrowsAsync<ResourceStoreException>(() =>
                store.UploadAsync("client", "b.wav", "audio/wav", new MemoryStream([1])));
            Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, full.Code);
            var foreign = await registry.UploadAssetAsync("other", new("request", "host", DateTimeOffset.UtcNow.AddMinutes(1)), candidate);
            Assert.Equal(BackendErrorCode.ResourceUnauthorized, foreign.Error!.Code);
        }
        Assert.Equal(0, store.UsedBytes);
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        StoredAsset replacement = await store.UploadAsync("client", "replacement.wav", "audio/wav", new MemoryStream([4, 3, 2, 1]));
        Assert.Equal(4, replacement.Size);
    }

    [Fact]
    public void 资源字节限额支持空值和Int64并拒绝非法组合()
    {
        using TemporaryDirectory directory = new();
        new BackendHostOptions { ResourceRoot = directory.Path }.Validate();
        new BackendHostOptions { ResourceRoot = directory.Path, MaxAssetBytes = (long)int.MaxValue + 1 }.Validate();
        new BackendHostOptions { ResourceRoot = directory.Path, MaxAssetQuotaBytes = (long)int.MaxValue + 1 }.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackendHostOptions { ResourceRoot = directory.Path, MaxAssetBytes = 0 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackendHostOptions { ResourceRoot = directory.Path, MaxAssetQuotaBytes = -1 }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackendHostOptions { ResourceRoot = directory.Path, MaxAssetBytes = 5, MaxAssetQuotaBytes = 4 }.Validate());
    }

    [Fact]
    public async Task 上传发布关闭独占写入句柄并提供完整固定内容()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        byte[] bytes = Enumerable.Range(0, 70_001).Select(index => (byte)index).ToArray();

        StoredAsset asset = await store.UploadAsync("client", "song.wav", "audio/wav", new MemoryStream(bytes));

        Assert.Equal(bytes, await File.ReadAllBytesAsync(asset.StoragePath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), asset.Sha256);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.upload", SearchOption.AllDirectories));
        Assert.True(store.TryGetAuthorized("client", asset.AssetId, out _));
    }

    [Fact]
    public async Task 并发上传的暂存字节参与配额且拒绝不减少既有占用()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 4, MaxAssetQuotaBytes = 6 });
        using HoldingUploadStream content = new([1, 2, 3, 4]);
        Task<StoredAsset> pending = store.UploadAsync("client", "a.wav", "audio/wav", content);
        try
        {
            await content.ReachedEof.Task.WaitAsync(TimeSpan.FromSeconds(5));
            ResourceStoreException rejected = await Assert.ThrowsAsync<ResourceStoreException>(() =>
                store.UploadAsync("client", "b.wav", "audio/wav", new MemoryStream([5, 6, 7])));
            Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, rejected.Code);
            Assert.Single(Directory.GetFiles(directory.Path, "*.upload", SearchOption.AllDirectories));
        }
        finally { content.Release(); }

        StoredAsset first = await pending;
        await store.UploadAsync("client", "c.wav", "audio/wav", new MemoryStream([5, 6]));
        ResourceStoreException full = await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client", "d.wav", "audio/wav", new MemoryStream([7])));
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, full.Code);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(first.StoragePath));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.upload", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task 暂存上传取消释放本次配额和文件()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 6, MaxAssetQuotaBytes = 6 });
        using HoldingUploadStream content = new([1, 2, 3, 4]);
        using CancellationTokenSource cancellation = new();
        Task<StoredAsset> pending = store.UploadAsync("client", "cancel.wav", "audio/wav", content, cancellation.Token);
        await content.ReachedEof.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        StoredAsset replacement = await store.UploadAsync("client", "replacement.wav", "audio/wav", new MemoryStream(new byte[6]));
        Assert.Equal(6, replacement.Size);
    }

    [Fact]
    public async Task 输入读取失败释放已经预留的暂存配额()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 6, MaxAssetQuotaBytes = 6 });
        using EndActionStream content = new([1, 2, 3, 4], () => throw new IOException("injected read failure"));

        await Assert.ThrowsAsync<IOException>(() => store.UploadAsync("client", "fail.wav", "audio/wav", content));

        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        StoredAsset replacement = await store.UploadAsync("client", "replacement.wav", "audio/wav", new MemoryStream(new byte[6]));
        Assert.Equal(6, replacement.Size);
    }

    [Fact]
    public async Task 发布文件失败不登记资源并释放完整预留配额()
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 6, MaxAssetQuotaBytes = 6 });
        string? blockedPath = null;
        using EndActionStream content = new(new byte[6], () =>
        {
            string upload = Assert.Single(Directory.GetFiles(directory.Path, "*.upload", SearchOption.AllDirectories));
            string finalName = Path.GetFileNameWithoutExtension(upload)[1..] + ".wav";
            blockedPath = Path.Combine(Path.GetDirectoryName(upload)!, finalName);
            Directory.CreateDirectory(blockedPath);
        });

        Exception? failure = await Record.ExceptionAsync(() => store.UploadAsync("client", "fail.wav", "audio/wav", content));

        Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
        Assert.NotNull(blockedPath);
        Assert.False(store.TryGetAuthorized("client", Path.GetFileNameWithoutExtension(blockedPath), out _));
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
        Directory.Delete(blockedPath!);
        StoredAsset replacement = await store.UploadAsync("client", "replacement.wav", "audio/wav", new MemoryStream(new byte[6]));
        Assert.Equal(6, replacement.Size);
    }

    [Theory]
    [InlineData("audio/wav-extra")]
    [InlineData("application/x-audio/wav")]
    [InlineData("video/mp4-extra")]
    [InlineData("application/x-video/mp4")]
    public async Task MIME门禁拒绝包含允许值的非精确类型(string contentType)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });

        ResourceStoreException rejected = await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client", "song.wav", contentType, new MemoryStream([1])));

        Assert.Equal(BackendErrorCode.UnsupportedMediaType, rejected.Code);
        Assert.Empty(Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("video/mp4", "clip.mp4")]
    [InlineData("video/quicktime", "clip.mov")]
    [InlineData("video/webm", "clip.webm")]
    [InlineData("video/x-matroska", "clip.mkv")]
    public async Task 视频容器MIME按精确值上传且仍以固定字节资源保存(string contentType, string name)
    {
        using TemporaryDirectory directory = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path });
        StoredAsset asset = await store.UploadAsync("client", name, contentType, new MemoryStream([1, 2, 3]));
        Assert.Equal(contentType, asset.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(asset.StoragePath));
    }

    [Fact]
    public async Task 正在读取和工程引用保留字节且最后释放后TTL重启并回收配额()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 6, MaxAssetQuotaBytes = 6,
            UnreferencedAssetLifetimeSeconds = 2 }, clock);
        StoredAsset asset = await store.UploadAsync("client", "a.wav", "audio/wav", new MemoryStream([1, 2, 3, 4, 5, 6]));
        IDisposable project = store.AcquireReference("client", asset.AssetId);
        ResourceReadLease reading = store.AcquireRead("client", asset.AssetId);
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, store.CollectExpired());
        Assert.Equal(6, store.UsedBytes);
        project.Dispose();
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(0, store.CollectExpired());
        using MemoryStream bytes = new();
        await reading.Stream.CopyToAsync(bytes);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, bytes.ToArray());
        await reading.DisposeAsync();
        reading.Dispose();
        clock.Advance(TimeSpan.FromSeconds(2).Subtract(TimeSpan.FromTicks(1)));
        Assert.Equal(0, store.CollectExpired());
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(1, store.CollectExpired());
        Assert.Equal(0, store.UsedBytes);
        Assert.False(File.Exists(asset.StoragePath));
        Assert.Equal(BackendErrorCode.ResourceExpired, Assert.Throws<ResourceStoreException>(() =>
            store.AcquireRead("client", asset.AssetId)).Code);
        StoredAsset replacement = await store.UploadAsync("client", "new.wav", "audio/wav", new MemoryStream(new byte[6]));
        Assert.NotEqual(asset.AssetId, replacement.AssetId);
    }

    [Fact]
    public async Task 未引用上传TTL在新上传配额判断前回收且数量同样有界()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ResourceStore store = new(new() { ResourceRoot = directory.Path, MaxAssetBytes = 4, MaxAssetQuotaBytes = 4,
            MaxAssets = 1, UnreferencedAssetLifetimeSeconds = 2 }, clock);
        StoredAsset old = await store.UploadAsync("client", "a.wav", "audio/wav", new MemoryStream([1, 2, 3, 4]));
        Assert.Equal(BackendErrorCode.ResourceQuotaExceeded, (await Assert.ThrowsAsync<ResourceStoreException>(() =>
            store.UploadAsync("client", "b.wav", "audio/wav", new MemoryStream([1])))).Code);
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, Assert.Throws<ResourceStoreException>(() =>
            store.AcquireRead("other", old.AssetId)).Code);
        clock.Advance(TimeSpan.FromSeconds(2));
        StoredAsset next = await store.UploadAsync("client", "b.wav", "audio/wav", new MemoryStream([5, 6, 7, 8]));
        Assert.False(File.Exists(old.StoragePath));
        Assert.True(File.Exists(next.StoragePath));
        Assert.Equal(4, store.UsedBytes);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private sealed class EndActionStream(byte[] bytes, Action atEnd) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read == 0) atEnd();
            return read;
        }
    }

    private sealed class HoldingUploadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReachedEof { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await base.ReadAsync(buffer, cancellationToken);
            if (read != 0) return read;
            ReachedEof.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return 0;
        }
    }
}
