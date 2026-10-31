using System.Security.Cryptography;
using MakoKaraGenerator.Backend.Contracts;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class WorkerResourceLeaseStoreTests
{
    [Fact]
    public async Task StageCreatesPrivateGrantAndReleaseRemovesIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "mako-worker-resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            using WorkerResourceLeaseStore store = new(root);
            byte[] bytes = [1, 2, 3, 4];
            using WorkerResourceLease lease = await store.StageAsync("p", "r", "assets/a.png", "image", bytes);
            Assert.True(store.HasLease(lease.GrantId));
            Assert.True(Path.IsPathFullyQualified(lease.Grant.FilePath));
            Assert.DoesNotContain(lease.Grant.FilePath, lease.Grant.PackagePath, StringComparison.Ordinal);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), lease.Grant.Sha256);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(lease.Grant.FilePath));
            string privatePath = lease.Grant.FilePath;
            lease.Dispose();
            Assert.False(store.HasLease(lease.GrantId));
            Assert.False(File.Exists(privatePath));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task UnsupportedRoleIsRejectedWithoutStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "mako-worker-resource-" + Guid.NewGuid().ToString("N"));
        try
        {
            using WorkerResourceLeaseStore store = new(root);
            WorkerProtocolException error = await Assert.ThrowsAsync<WorkerProtocolException>(() =>
                store.StageAsync("p", "r", "audio.wav", "audio", new byte[] { 1 }));
            Assert.Equal("WorkerResourceRoleUnsupported", error.Code);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
