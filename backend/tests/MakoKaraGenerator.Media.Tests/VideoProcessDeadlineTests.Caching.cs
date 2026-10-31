namespace MakoKaraGenerator.Media.Tests;

public sealed partial class VideoProcessDeadlineTests
{
    [Fact]
    public void 缓存同帧及少量前进不重启真正回退和远跳才重启()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded(); using var source = provider.Open(child.Input, out _)!;
        foreach (long time in new[] { 0L, 16_666L, 33_333L, 50_000L, 66_666L, 83_333L, 100_000L })
            Assert.True(source.TryGetFrame(time, out _), source.LastError);
        Assert.Single(File.ReadAllLines(child.Input + ".decoder.starts"));
        Assert.True(source.TryGetFrame(0, out var backward)); Assert.Equal(0, backward!.Pixels[0]);
        Assert.Equal(2, File.ReadAllLines(child.Input + ".decoder.starts").Length);
        Assert.True(source.TryGetFrame(1_500_000, out var far)); Assert.Equal(45, far!.Pixels[0]);
        Assert.Equal(3, File.ReadAllLines(child.Input + ".decoder.starts").Length);
        Assert.True(source.TryGetFrame(1_500_000, out _)); Assert.Equal(3, File.ReadAllLines(child.Input + ".decoder.starts").Length);
    }

    [Fact]
    public void 缓存后半帧超时不能返回旧成功缓冲而重试必须重新解码()
    {
        using var child = new MediaChild("one-frame-then-half"); using var provider = Bounded(); using var source = provider.Open(child.Input, out _)!;
        Assert.True(source.TryGetFrame(0, out var first)); Assert.Equal(0, first!.Pixels[0]);
        Assert.False(source.TryGetFrame(50_000, out var failed)); Assert.Null(failed); Assert.Contains("超时", source.LastError, StringComparison.Ordinal);
        child.SetMode("normal"); Assert.True(source.TryGetFrame(0, out var recovered));
        Assert.Equal(new byte[] { 0, 2, 3, 255 }, recovered!.Pixels); Assert.Null(source.LastError);
        Assert.Equal(2, File.ReadAllLines(child.Input + ".decoder.starts").Length);
    }

    [Fact]
    public void 缓存命中仍检查调用者取消并使后续重试拥有新解码器()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded(); using var source = provider.Open(child.Input, out _)!;
        Assert.True(source.TryGetFrame(0, out _)); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => source.TryGetFrame(0, out _, cancellation.Token));
        Assert.True(source.TryGetFrame(0, out var recovered)); Assert.Equal(0, recovered!.Pixels[0]);
        Assert.Equal(2, File.ReadAllLines(child.Input + ".decoder.starts").Length);
    }

    [Fact]
    public void 缓存释放后不返回旧帧且不启动解码器()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded(); using var source = provider.Open(child.Input, out _)!;
        Assert.True(source.TryGetFrame(0, out _)); source.Dispose(); source.Dispose();
        Assert.False(source.TryGetFrame(0, out var after)); Assert.Null(after);
        Assert.Contains("释放", source.LastError, StringComparison.Ordinal); Assert.Single(File.ReadAllLines(child.Input + ".decoder.starts"));
    }

    [Fact]
    public void 缓存保持原像素借用合同及同提供者各来源缓冲隔离()
    {
        using var child = new MediaChild("normal"); using var provider = Bounded();
        using var first = provider.Open(child.Input, out _)!; using var second = provider.Open(child.Input, out _)!;
        Assert.True(first.TryGetFrame(0, out var borrowed)); byte[] copied = [.. borrowed!.Pixels];
        Assert.True(first.TryGetFrame(50_000, out var next)); Assert.Same(borrowed, next); Assert.Equal(1, borrowed.Pixels[0]); Assert.Equal(0, copied[0]);
        Assert.True(second.TryGetFrame(100_000, out var other)); Assert.NotSame(next!.Pixels, other!.Pixels); Assert.Equal(3, other.Pixels[0]);
        Assert.True(first.TryGetFrame(50_000, out var retained)); Assert.Equal(1, retained!.Pixels[0]);
        Assert.Equal(2, File.ReadAllLines(child.Input + ".decoder.starts").Length);
    }
}
