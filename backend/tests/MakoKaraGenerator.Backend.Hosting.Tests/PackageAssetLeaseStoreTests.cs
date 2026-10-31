using MakoKaraGenerator.Backend.Hosting;
using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PackageAssetLeaseStoreTests
{
    [Fact]
    public async Task 包资源投影复用asset并在工程关闭后按TTL释放且保留ACL()
    {
        using TemporaryDirectory directory = new();
        Clock clock = new();
        ResourceStore resources = new(new()
        {
            ResourceRoot = directory.Path,
            MaxAssetBytes = 1024,
            MaxAssetQuotaBytes = 4096,
            UnreferencedAssetLifetimeSeconds = 2,
        }, clock);
        using PackageAssetLeaseStore packages = new(resources);
        byte[] content = [1, 2, 3, 4];

        StoredAsset first = await packages.MaterializeAsync("owner", "project-1", "packageFile:rev:path.txt",
            "path.txt", "asset", content);
        StoredAsset second = await packages.MaterializeAsync("owner", "project-1", "packageFile:rev:path.txt",
            "path.txt", "asset", content);

        Assert.Equal(first.AssetId, second.AssetId);
        Assert.True(packages.HasSessionLease("project-1"));
        Assert.True(resources.HasClientReferences("owner"));
        Assert.Equal(BackendErrorCode.ResourceUnauthorized, Assert.Throws<ResourceStoreException>(() =>
            resources.AcquireRead("other", first.AssetId)).Code);
        using (ResourceReadLease read = resources.AcquireRead("owner", first.AssetId))
        using (MemoryStream copy = new())
        {
            await read.Stream.CopyToAsync(copy);
            Assert.Equal(content, copy.ToArray());
        }

        packages.ReleaseSession("project-1");
        Assert.False(packages.HasSessionLease("project-1"));
        Assert.False(resources.HasClientReferences("owner"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, resources.CollectExpired());
        Assert.False(resources.TryGetAuthorized("owner", first.AssetId, out _));
    }

    [Fact]
    public async Task 已验证图片包资源按扩展名投影为可读取的图片asset()
    {
        using TemporaryDirectory directory = new();
        ResourceStore resources = new(new() { ResourceRoot = directory.Path });
        using PackageAssetLeaseStore packages = new(resources);
        byte[] content = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        StoredAsset asset = await packages.MaterializeAsync("owner", "project-image", "packageFile:rev:images/actual.png",
            "images/actual.png", "image", content);

        Assert.Equal("image/png", asset.ContentType);
        using ResourceReadLease read = resources.AcquireRead("owner", asset.AssetId);
        using MemoryStream copy = new();
        await read.Stream.CopyToAsync(copy);
        Assert.Equal(content, copy.ToArray());
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan value) => _now += value;
    }
}
