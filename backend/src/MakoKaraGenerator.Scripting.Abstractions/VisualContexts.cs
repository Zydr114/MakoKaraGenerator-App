using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>文字求值完成后的真实场景几何；不包含可写帧输出或原生资源。</summary>
public sealed record FinalSceneGeometry
{
    /// <summary>歌曲整数微秒时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>已按输出绑定筛选的真实对象；组目录只读。</summary>
    public required FramePresentations Presentations { get; init; }
    /// <summary>最终元素族及其全部注音，保留真实装载。</summary>
    public ImmutableArray<TokenGeometrySample> Tokens { get; init; } = [];
    /// <summary>最终整行矩阵，包括没有逐字元素的行。</summary>
    public ImmutableArray<LineGeometrySample> Lines { get; init; } = [];
    /// <summary>最终真实提示标记。</summary>
    public ImmutableArray<TextMarkGeometrySample> Marks { get; init; } = [];
    /// <summary>全部真实组的只读最终参考框与墨迹，读取不授予写权。</summary>
    public ImmutableArray<GroupGeometrySample> Groups { get; init; } = [];
    /// <summary>绑定范围内的全部真实播放头；后端不可用时仍明确不可用。</summary>
    public PlaybackFrame Playback { get; init; } = PlaybackFrame.Unavailable;
}
