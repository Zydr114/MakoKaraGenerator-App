using System.Diagnostics;
using MakoKaraGenerator.Rendering.Abstractions;

namespace MakoKaraGenerator.Media.Tests;

public sealed partial class VideoProcessDeadlineTests
{
    private static FfmpegVideoFrameProvider Bounded(TimeSpan? timeout = null) =>
        new(MediaChild.Executable, MediaChild.Executable, timeout ?? TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("probe-invalid", "元数据")]
    [InlineData("probe-overflow", "大小")]
    [InlineData("probe-error", "退出码 7")]
    [InlineData("probe-late", "超时")]
    public async Task 元数据失败有明确分类且有限回收(string mode, string expected)
    {
        using var child = new MediaChild(mode);
        using var provider = mode == "probe-late" ? Bounded(TimeSpan.FromMilliseconds(1200)) : Bounded();
        Assert.Null(provider.Open(child.Input, out string? error)); Assert.Contains(expected, error, StringComparison.Ordinal);
        Assert.True(error!.Length < 34_000); await child.AssertExited("probe");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 调用者在管道阻塞时取消仍抛取消且本次进程退出(bool frame)
    {
        using var child = new MediaChild(frame ? "half-frame" : "probe-stall"); using var provider = Bounded(TimeSpan.FromSeconds(4));
        using var source = frame ? provider.Open(child.Input, out _) : null;
        using var cancellation = new CancellationTokenSource();
        var operation = Task.Run(() => frame ? source!.TryGetFrame(0, out _, cancellation.Token)
            : provider.Open(child.Input, out _, cancellation.Token) is not null);
        await Until(() => File.Exists(child.Input + (frame ? ".decoder.pid" : ".probe.pid")));
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(cancellation.Token, error.CancellationToken); await child.AssertExited(frame ? "decoder" : "probe");
    }

    [Theory]
    [InlineData("drip-frame")]
    [InlineData("late-frame")]
    [InlineData("slow-sequence")]
    public async Task 一次取帧期限覆盖全部字节及跳过的多个完整帧(string mode)
    {
        using var child = new MediaChild(mode); using var provider = Bounded(TimeSpan.FromMilliseconds(1200)); using var source = provider.Open(child.Input, out _)!;
        if (mode == "slow-sequence") Assert.True(source.TryGetFrame(0, out _));
        var watch = Stopwatch.StartNew();
        Assert.False(source.TryGetFrame(mode == "slow-sequence" ? 600_000 : 0, out var frame));
        Assert.Null(frame); Assert.Contains("超时", source.LastError, StringComparison.Ordinal);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(4)); await child.AssertExited("decoder");
    }

    [Theory]
    [InlineData("decoder-error")]
    [InlineData("decoder-error-flood")]
    public async Task 解码错误保留有限诊断且没有陈旧帧(string mode)
    {
        using var child = new MediaChild(mode); using var provider = Bounded(); using var source = provider.Open(child.Input, out _)!;
        Assert.False(source.TryGetFrame(0, out var frame)); Assert.Null(frame); Assert.True(source.LastError!.Length < 34_000);
        if (mode == "decoder-error") Assert.Contains("controlled decoder failure", source.LastError, StringComparison.Ordinal);
        else Assert.Contains("后续诊断已省略", source.LastError, StringComparison.Ordinal);
        await child.AssertExited("decoder");
    }

    [Fact]
    public async Task 超时后的同一来源重试得到完整新帧而不发布半帧()
    {
        using var child = new MediaChild("half-frame"); using var provider = Bounded(TimeSpan.FromMilliseconds(1200)); using var source = provider.Open(child.Input, out _)!;
        Assert.False(source.TryGetFrame(0, out _)); await child.AssertExited("decoder");
        child.SetMode("normal"); Assert.True(source.TryGetFrame(0, out var frame)); Assert.Equal(new byte[] { 0, 2, 3, 255 }, frame!.Pixels);
        Assert.Null(source.LastError); source.Dispose(); await child.AssertExited("decoder");
    }

    [Fact]
    public void 预先取消不启动探测也不落入缺失背景路径()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
#pragma warning disable CA1859 // Exercise the host interface dispatch, not only the concrete overload.
        IVideoFrameProvider host = provider;
#pragma warning restore CA1859
        Assert.ThrowsAny<OperationCanceledException>(() => host.Open(child.Input, out _, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => host.Open(child.Input + "missing", out _, cancellation.Token));
        Assert.False(File.Exists(child.Input + ".probe.pid"));
    }

    [Fact]
    public async Task 同步桥不向调用者同步上下文投递续体()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded(TimeSpan.FromSeconds(2));
        await Task.Run(() =>
        {
            var before = SynchronizationContext.Current; var context = new RejectingContext(); SynchronizationContext.SetSynchronizationContext(context);
            try { using var source = provider.Open(child.Input, out _)!; Assert.True(source.TryGetFrame(0, out _)); Assert.Equal(0, context.Posts); }
            finally { SynchronizationContext.SetSynchronizationContext(before); }
        }).WaitAsync(TimeSpan.FromSeconds(4));
        await child.AssertExited("probe"); await child.AssertExited("decoder");
    }

    [Fact]
    public async Task 重复失败关闭释放管道句柄且二次释放幂等()
    {
        for (int index = 0; index < 8; index++)
        {
            using var child = new MediaChild("probe-stall");
            using var process = OwnedMediaProcess.Start(MediaChild.Executable, [child.Input]);
            Stream output = process.Output;
            using var cancellation = new CancellationTokenSource();
            var reading = process.ReadMetadataAsync(cancellation.Token);
            await Until(() => File.Exists(child.Input + ".probe.pid"));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(TimeSpan.FromSeconds(2)));
            await process.CloseAsync(); process.Dispose();
            Assert.Throws<ObjectDisposedException>(() => output.ReadByte());
            Assert.True(process.Errors.IsCompletedSuccessfully);
            await child.AssertExited("probe");
        }
    }

    [Fact]
    public async Task 取消回收自有后代及继承的管道而不触碰另一个媒体进程()
    {
        using var child = new MediaChild("probe-descendant"); using var unrelated = new MediaChild("probe-stall");
        var info = new ProcessStartInfo(MediaChild.Executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(unrelated.Input); using var other = Process.Start(info)!;
        using var provider = Bounded(TimeSpan.FromSeconds(4)); using var cancellation = new CancellationTokenSource();
        var operation = Task.Run(() => provider.Open(child.Input, out _, cancellation.Token));
        try
        {
            await Until(() => File.Exists(child.Input + ".descendant.pid"));
            cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
            await child.AssertExited("probe"); await child.AssertExited("descendant"); Assert.False(other.HasExited);
        }
        finally { cancellation.Cancel(); child.Stop(); unrelated.Stop(); await other.WaitForExitAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 取消与自然退出竞争明确报告并释放本次进程(bool cancel)
    {
        using var child = new MediaChild("probe-gated-exit"); using var provider = Bounded(TimeSpan.FromSeconds(3));
        using var cancellation = new CancellationTokenSource();
        var operation = Task.Run(() => provider.Open(child.Input, out _, cancellation.Token));
        await Until(() => File.Exists(child.Input + ".probe.pid"));
        if (cancel) cancellation.Cancel(); File.WriteAllText(child.Input + ".release", "done");
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(2)));
        else { using var source = await operation.WaitAsync(TimeSpan.FromSeconds(2)); Assert.NotNull(source); }
        await child.AssertExited("probe");
    }

    private sealed class RejectingContext : SynchronizationContext
    {
        internal int Posts { get; private set; }
        public override void Post(SendOrPostCallback d, object? state) { Posts++; throw new InvalidOperationException("captured caller context"); }
    }
    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew(); while (!predicate() && watch.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        Assert.True(predicate());
    }
}
