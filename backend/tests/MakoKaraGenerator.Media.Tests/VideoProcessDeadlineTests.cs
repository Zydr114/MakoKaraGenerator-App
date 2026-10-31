using System.Diagnostics;
using System.Text.Json;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media.Tests;

public sealed partial class VideoProcessDeadlineTests
{
    [Theory]
    [InlineData("probe-stall")]
    [InlineData("probe-full-stderr")]
    public async Task 探测期限覆盖标准输出停滞且同时排空错误管道(string mode)
    {
        using var child = new MediaChild(mode);
        using var provider = new FfmpegVideoFrameProvider(MediaChild.Executable, MediaChild.Executable);
        Task<IVideoFrameSource?> operation = Task.Run(() => provider.Open(child.Input, out _));
        try
        {
            using var source = await operation.WaitAsync(TimeSpan.FromSeconds(12));
            if (mode == "probe-stall") Assert.Null(source);
            else Assert.NotNull(source);
            await child.AssertExited("probe");
        }
        finally
        {
            child.Stop();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
    }

    [Fact]
    public async Task 只返回半帧的真实子进程在一次取帧期限内终止回收()
    {
        using var child = new MediaChild("half-frame");
        using var provider = new FfmpegVideoFrameProvider(MediaChild.Executable, MediaChild.Executable);
        using var source = provider.Open(child.Input, out _)!;
        Assert.NotNull(source);
        Task<bool> operation = Task.Run(() => source.TryGetFrame(0, out _));
        try
        {
            Assert.False(await operation.WaitAsync(TimeSpan.FromSeconds(12)));
            Assert.Contains("超时", source.LastError, StringComparison.Ordinal);
            await child.AssertExited("decoder");
        }
        finally
        {
            child.Stop();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
    }

    private sealed class MediaChild : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        internal string Input { get; }
        internal static string Executable => Path.Combine(AppContext.BaseDirectory, "media-worker",
            "MakoKaraGenerator.Media.Worker" + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        internal void SetMode(string mode) => File.WriteAllText(Input, JsonSerializer.Serialize(new { mode }));
        internal int Pid(string kind) => int.Parse(File.ReadAllText(Input + "." + kind + ".pid"), System.Globalization.CultureInfo.InvariantCulture);
        internal MediaChild(string mode)
        {
            Input = _directory.FilePath("fixture.media"); SetMode(mode);
            Assert.True(File.Exists(Executable), "media worker must be built and copied");
        }
        internal async Task AssertExited(string kind)
        {
            string path = Input + "." + kind + ".pid"; Assert.True(File.Exists(path));
            int pid = int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);
            var watch = Stopwatch.StartNew();
            while (Alive(pid) && watch.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(10);
            Assert.False(Alive(pid), "owned child is still running: " + pid);
        }
        internal static bool Alive(int pid)
        {
            try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        internal void Stop()
        {
            foreach (string path in Directory.EnumerateFiles(_directory.Path, "*.pid"))
            {
                int pid = int.Parse(File.ReadAllText(path), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(3000)); }
                }
                catch (ArgumentException) { }
            }
        }
        public void Dispose() { Stop(); _directory.Dispose(); }
    }
}
