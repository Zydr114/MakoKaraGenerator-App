namespace MakoKaraGenerator.Scripting;

/// <summary>当前固定边界的纯发射输入；没有粒子、文字 writer 或未来布局查询。</summary>
public sealed record EmissionFrameContext : VisualContext
{
    private readonly TimelineHistory? _history;
    /// <summary>所有文字实例完成后的最终几何，按真实输出绑定筛选。</summary>
    public required FinalSceneGeometry FinalGeometry { get; init; }
    /// <summary>同一最终几何中的合法播放头。</summary>
    public PlaybackFrame Playback => FinalGeometry.Playback;
    /// <summary>当前固定边界的完整历史；纯帧读取明确失败。</summary>
    public TimelineHistory History
    {
        get => _history ?? throw new EffectOutputException("TimelineSessionRequired", "连续发射需要时间线会话。");
        init => _history = value;
    }
    /// <summary>实际求值时刻，正常时间线只在固定边界调用。</summary>
    public long TimeUs => FinalGeometry.TimeUs;
}
