using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendPreviewRetentionRetryTests
{
    [Fact]
    public void 共享预览租约在真实私有目录删除失败后保留重试所有权()
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.FilePath("prepared");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "media.bin"), "private-media");
        using BackendPreparedProjectRuntimeInput prepared = PreparedRuntimeInputRetentionRetryTests.Create(root);
        using BackendPreviewRetentionLease owner = Assert.IsType<BackendPreviewRetentionLease>(BackendPreviewRetentionLease.Own(prepared));
        using BackendPreviewRetentionLease last = owner.AcquireReference();
        owner.Dispose();
        Assert.True(Directory.Exists(root));

        string saved = temporary.FilePath("saved");
        Directory.Move(root, saved);
        File.WriteAllText(root, "filesystem-obstruction");
        try
        {
            Assert.ThrowsAny<IOException>(last.Dispose);
            Assert.Throws<ObjectDisposedException>(() => last.AcquireReference());
            Assert.True(File.Exists(Path.Combine(saved, "media.bin")));
        }
        finally
        {
            File.Delete(root);
            Directory.Move(saved, root);
        }

        last.Dispose();
        Assert.False(Directory.Exists(root));
        last.Dispose();
    }
}
