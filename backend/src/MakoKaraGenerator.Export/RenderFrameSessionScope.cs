using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Export;

// 成片发布前结束像素会话；外层异常路径保留第一失败，每个会话只释放一次。
internal sealed class RenderFrameSessionScope(IRenderFrameSession? session) : IAsyncDisposable
{
    private IRenderFrameSession? _session = session;
    public Exception? PrimaryFailure { get; set; }
    public async ValueTask DisposeAsync()
    {
        var previous = Interlocked.Exchange(ref _session, null);
        if (previous is null) return;
        try { await previous.DisposeAsync().ConfigureAwait(false); }
        catch (Exception error) when (PrimaryFailure is { } primary)
        {
            primary.Data["RenderSessionCleanupFailure"] = error;
            Console.Error.WriteLine("[VideoExporter] 像素会话释放失败：" + error);
        }
    }
}
