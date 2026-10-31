using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>歌曲整数时间线上的真实光标端点选择。</summary>
public enum CursorEndpointKind
{
    /// <summary>真实半开区间的最后可表示时刻 EndUs-1，不伪造进度一。</summary>
    PreviousEndLimit,
    /// <summary>真实起唱 StartUs，不使用提前可见起点。</summary>
    NextStart,
}

/// <summary>独立语义查询得到的不可变端点；装载不可见时 Cursor 为空。</summary>
/// <param name="Identity">请求的真实源与装载。</param><param name="Kind">端点选择。</param>
/// <param name="TimeUs">真正采样的整数时刻。</param><param name="AnchorMode">明确纵向锚点。</param>
/// <param name="Cursor">最终文字播放头；装载不在场时不可用。</param><param name="Diagnostics">独立会话的非失败诊断。</param>
public sealed record CursorEndpoint(CursorIdentity Identity, CursorEndpointKind Kind, long TimeUs, CursorAnchorMode AnchorMode,
    PlaybackCursor? Cursor, ImmutableArray<RuntimeDiagnostic> Diagnostics)
{
    /// <summary>实际装载在端点时刻可见且确实处于演唱。</summary>
    public bool IsAvailable => Cursor is not null;
}
