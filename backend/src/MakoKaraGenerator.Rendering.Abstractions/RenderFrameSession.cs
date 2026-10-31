namespace MakoKaraGenerator.Rendering;

/// <summary>组合根提供的独立像素会话工厂，固定资源与作者对象不由绘制控件创建。</summary>
public interface IRenderFrameSessionFactory
{
    /// <summary>为不可变快照建立独立资源／准备／绘制会话；失败不能使用旧输入代替。</summary>
    ValueTask<IRenderFrameSession> CreateAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken = default);
}

/// <summary>先准备后绘制的串行像素会话，调用方拥有每份返回帧。</summary>
public interface IRenderFrameSession : IAsyncDisposable
{
    /// <summary>求值和绘制同一快照／时刻／密度／输出模式，不在UI线程调用作者。</summary>
    ValueTask<RenderFrame> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default);
}
