using System.Reflection;
using System.Runtime.ExceptionServices;
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
        object store = Create(root, beforeFlush); object? reservation = reserved ? Invoke(store, "TryReserve") : null;
        byte[] bytes = new byte[768]; bytes[0] = 19;
        try
        {
            Exception? actual = Record.Exception(() => Invoke(store, "Prepare", Job(), bytes, reservation));
            Assert.Same(primary, actual); Assert.IsAssignableFrom<UnauthorizedAccessException>(primary.Data["JobSidecarCleanupFailure"]);
            (reservation as IDisposable)?.Dispose(); reservation = null;
            Assert.NotNull(temporary); Assert.Equal(bytes, File.ReadAllBytes(temporary));
            Assert.Throws<UnauthorizedAccessException>(() => File.Delete(temporary));
            Assert.Equal(bytes.LongLength, Used(store)); Assert.Null(Invoke(store, "TryReserve"));
            Assert.Throws<UnauthorizedAccessException>(() => Invoke(store, "Prune", (object)Array.Empty<string>()));
            Assert.Equal(bytes.LongLength, Used(store)); Assert.True(File.Exists(temporary));
            Restore(root, temporary); Invoke(store, "Prune", (object)Array.Empty<string>());
            Assert.False(File.Exists(temporary)); Assert.Equal(0, Used(store));
            using IDisposable next = (IDisposable)Invoke(store, "TryReserve")!; Assert.Equal(1024, Used(store));
        }
        finally { (reservation as IDisposable)?.Dispose(); Restore(root, temporary); }
    }
    [Fact]
    public void 重启按每个遗留临时路径计费且同job多个tmp不覆盖已发布报告身份()
    {
        using TemporaryDirectory directory = new(); string root = directory.FilePath("sidecars"); Directory.CreateDirectory(root);
        string job = Job(); byte[] published = new byte[128]; published[0] = 33;
        object original = Create(root); BackendJobReportReferenceDto reference = (BackendJobReportReferenceDto)Invoke(original, "Publish", job, published)!;
        string first = Path.Combine(root, reference.ReportId + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        string second = Path.Combine(root, reference.ReportId + ".json." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllBytes(first, new byte[256]); File.WriteAllBytes(second, new byte[512]);
        object restarted = Create(root);
        try
        {
            Assert.Equal(896, Used(restarted)); Assert.Null(Invoke(restarted, "TryReserve"));
            Assert.Equal(published, (byte[])Invoke(restarted, "Read", job, reference)!);
            BlockDelete(root, first); BlockDelete(root, second);
            Assert.Throws<UnauthorizedAccessException>(() => Invoke(restarted, "Prune", (object)new[] { reference.ReportId }));
            Assert.Equal(896, Used(restarted)); Assert.True(File.Exists(first)); Assert.True(File.Exists(second));
            Assert.Equal(published, (byte[])Invoke(restarted, "Read", job, reference)!);
            Restore(root, first); Restore(root, second); Invoke(restarted, "Prune", (object)new[] { reference.ReportId });
            Assert.Equal(published.LongLength, Used(restarted)); Assert.False(File.Exists(first)); Assert.False(File.Exists(second));
            Assert.Equal(published, (byte[])Invoke(restarted, "Read", job, reference)!);
        }
        finally { Restore(root, first); Restore(root, second); }
    }
    private static string Job() => "job-" + Guid.NewGuid().ToString("N");
    private static object Create(string root, Action<string>? beforeFlush = null) => Activator.CreateInstance(
        typeof(ProjectSessionRegistry).Assembly.GetType("MakoKaraGenerator.Backend.Hosting.BackendJobSidecarStore")!,
        BindingFlags.Instance | BindingFlags.NonPublic, null, [root, "report-", "AnalysisResult", 1024, 1024L, beforeFlush], null)!;
    private static object? Invoke(object store, string name, params object?[] args)
    {
        try { return store.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(store, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static long Used(object store) => (long)store.GetType().GetProperty("UsedBytes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
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
