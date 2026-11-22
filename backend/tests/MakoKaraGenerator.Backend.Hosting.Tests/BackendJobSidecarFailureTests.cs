using MakoKaraGenerator.Backend.Contracts;
using MakoKaraGenerator.TestSupport;

namespace MakoKaraGenerator.Backend.Hosting.Tests;

public sealed class BackendJobSidecarFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 真实临时文件写入主失败叠实际删除失败仍计费直到prune成功(bool reserved)
    {
        using TemporaryDirectory directory = new(); string root = directory.FilePath("sidecars"); Directory.CreateDirectory(root);
        IOException primary = new("controlled flush failure"); string? temporary = null;
        Action<string> beforeFlush = path => { temporary = path; BlockDelete(root, path); throw primary; };
        BackendJobSidecarStore store = Create(root, beforeFlush); BackendJobSidecarStore.Reservation? reservation = reserved ? store.TryReserve() : null;
        byte[] bytes = new byte[768]; bytes[0] = 19;
        try
        {
            Exception? actual = Record.Exception(() => store.Prepare(Job(), bytes, reservation));
            Assert.Same(primary, actual); Assert.IsAssignableFrom<UnauthorizedAccessException>(primary.Data["JobSidecarCleanupFailure"]);
            reservation?.Dispose(); reservation = null;
            Assert.NotNull(temporary); Assert.Equal(bytes, File.ReadAllBytes(temporary));
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(temporary));
            Assert.Equal(bytes.LongLength, store.UsedBytes); Assert.Null(store.TryReserve());
            Assert.Throws<UnauthorizedAccessException>(() => store.Prune(Array.Empty<string>()));
            Assert.Equal(bytes.LongLength, store.UsedBytes); Assert.True(File.Exists(temporary));
            Restore(root, temporary); store.Prune(Array.Empty<string>());
            Assert.False(File.Exists(temporary)); Assert.Equal(0, store.UsedBytes);
            using BackendJobSidecarStore.Reservation next = store.TryReserve()!; Assert.Equal(1024, store.UsedBytes);
        }
        finally { reservation?.Dispose(); Restore(root, temporary); }
    }
    [Fact]
    public void 重启按每个遗留临时路径计费且同job多个tmp不覆盖已发布报告身份()
    {
        using TemporaryDirectory directory = new(); string root = directory.FilePath("sidecars"); Directory.CreateDirectory(root);
        string job = Job(); byte[] published = new byte[128]; published[0] = 33;
        BackendJobSidecarStore original = Create(root); BackendJobReportReferenceDto reference = original.Publish(job, published);
        string first = Path.Combine(root, reference.ReportId + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        string second = Path.Combine(root, reference.ReportId + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(first, new byte[256]); File.WriteAllBytes(second, new byte[512]);
        BackendJobSidecarStore restarted = Create(root);
        try
        {
            Assert.Equal(896, restarted.UsedBytes); Assert.Null(restarted.TryReserve());
            Assert.Equal(published, restarted.Read(job, reference));
            BlockDelete(root, first); BlockDelete(root, second);
            Assert.Throws<UnauthorizedAccessException>(() => restarted.Prune([reference.ReportId]));
            Assert.Equal(896, restarted.UsedBytes); Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
            Assert.Equal(published, restarted.Read(job, reference));
            Restore(root, first); Restore(root, second); restarted.Prune([reference.ReportId]);
            Assert.Equal(published.LongLength, restarted.UsedBytes); Assert.False(File.Exists(first)); Assert.False(File.Exists(second));
            Assert.Equal(published, restarted.Read(job, reference));
        }
        finally { Restore(root, first); Restore(root, second); }
    }
    private static string Job() => "job-" + Guid.NewGuid().ToString("N");
    private static BackendJobSidecarStore Create(string root, Action<string>? beforeFlush = null) =>
        new(root, "report-", "AnalysisResult", 1024, 1024L, beforeFlush);
    private static void BlockDelete(string root, string path)
    {
        if (OperatingSystem.IsWindows()) File.SetAttributes(path, FileAttributes.ReadOnly);
        else File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }
    private static void Restore(string root, string? path)
    {
        if (OperatingSystem.IsWindows()) { if (path is not null && File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); }
        else File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
