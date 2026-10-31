using MakoKaraGenerator.Core.Projects;

namespace MakoKaraGenerator.Rendering;

/// <summary>同一已测量场景直接绘制的像素与中立几何；拥有像素帧。</summary>
public sealed record MeasuredRenderFrame(RenderFrame Frame, SceneFrame Scene,
    IReadOnlyDictionary<string, SceneTreeGeometry> Nodes,
    IReadOnlyDictionary<string, SceneTreeGeometry> CueTargetReferences) : IDisposable
{
    /// <inheritdoc/>
    public void Dispose() => Frame.Dispose();
}

/// <summary>静态源的同帧测量能力；效果包不通过此接口扩展。</summary>
public interface ISceneMeasurementSession
{
    /// <summary>固定静态快照的一次同帧几何与像素测量；布局模式不取播放时刻。</summary>
    ValueTask<MeasuredRenderFrame> MeasureAsync(RenderRequest request, bool layoutReference,
        string? editingLineId = null, string? editingCueId = null, CancellationToken cancellationToken = default);
}
