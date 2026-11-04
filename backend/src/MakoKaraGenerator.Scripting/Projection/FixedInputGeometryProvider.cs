using MakoKaraGenerator.Rendering;

namespace MakoKaraGenerator.Scripting.Projection;

/// <summary>同一串行固定输入会话共享基础场景；作者查询和正式文字准备复用精确时刻采样。</summary>
internal sealed class FixedInputGeometryProvider(ProjectSnapshot snapshot, ILineGeometryProvider source) : ILineGeometryProvider, IDisposable
{
    private const int MaximumEntries = 8;
    private const int MaximumValues = 400_000;
    private readonly Dictionary<long, Entry> _frames = [];
    private int _values;
    private long _stamp;
    private bool _disposed;
    private sealed record Entry(SceneFrame Frame, int Weight, long Stamp);

    public SceneFrame GetSceneFrame(ProjectSnapshot input, long timeUs, string? editingLineId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(input, snapshot) || editingLineId is not null)
            throw new PreparedFrameException("EffectInputsMismatch", "固定基础布局缓存不能切换输入或使用编辑试样。");
        ArgumentOutOfRangeException.ThrowIfNegative(timeUs);
        if (_frames.TryGetValue(timeUs, out var found))
        {
            _frames[timeUs] = found with { Stamp = ++_stamp };
            return found.Frame;
        }
        var frame = source.GetSceneFrame(input, timeUs);
        long weight = (long)frame.Elements.Length + frame.Lines.Length + frame.TokenGeometries.Length
            + frame.LineGeometries.Length + frame.MarkGeometries.Length + frame.GroupGeometries.Length;
        foreach (var item in frame.TokenGeometries)
            weight += item.Clusters.Length + item.BaseGeometry.DrawUnits.Length + item.Ruby.Sum(static ruby => (long)ruby.Geometry.DrawUnits.Length);
        if (weight > MaximumValues) return frame;
        while (_frames.Count >= MaximumEntries || _values + weight > MaximumValues)
        {
            var oldest = _frames.MinBy(static item => item.Value.Stamp);
            _values -= oldest.Value.Weight; _frames.Remove(oldest.Key);
        }
        _frames.Add(timeUs, new(frame, (int)weight, ++_stamp)); _values += (int)weight;
        return frame;
    }

    public LineRenderGeometry? GetLineGeometry(ProjectSnapshot input, string lineId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(input, snapshot)) throw new PreparedFrameException("EffectInputsMismatch", "基础几何不属于固定输入。");
        return source.GetLineGeometry(input, lineId);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _frames.Clear(); _values = 0;
        (source as IDisposable)?.Dispose();
    }
}
