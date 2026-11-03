using MakoKaraGenerator.Core.Lyrics;
using MakoKaraGenerator.Core.Projects;
using MakoKaraGenerator.Core.Time;
using MakoKaraGenerator.Rendering;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class PreparedRuntimeInputRetentionRetryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 私有目录真实删除失败保留最后引用且重试可完成(bool lastIsAcquiredReference)
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.FilePath("prepared");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "media.bin"), "private-media");
        using BackendPreparedProjectRuntimeInput prepared = Create(root);
        IDisposable last = lastIsAcquiredReference ? prepared.AcquireReference() : prepared;
        if (lastIsAcquiredReference) prepared.Dispose();

        string saved = temporary.FilePath("saved");
        Directory.Move(root, saved);
        File.WriteAllText(root, "filesystem-obstruction");
        try
        {
            Assert.ThrowsAny<IOException>(last.Dispose);
            Assert.Throws<ObjectDisposedException>(() => prepared.AcquireReference());
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

    [Fact]
    public void 私有目录已被删除视为清理完成且禁止新引用()
    {
        using TemporaryDirectory temporary = new();
        string root = temporary.FilePath("prepared");
        Directory.CreateDirectory(root);
        using BackendPreparedProjectRuntimeInput prepared = Create(root);
        Directory.Delete(root);

        prepared.Dispose();
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.AcquireReference());
    }

    internal static BackendPreparedProjectRuntimeInput Create(string root) => new(
        ProjectSnapshot.FromProject(Project.Create("retention-retry",
            Composition.Create(16, 8, new FrameRate(4, 1), outputDurationUs: 1_000_000),
            LyricsDocument.Create([]))), [], null, root);
}
