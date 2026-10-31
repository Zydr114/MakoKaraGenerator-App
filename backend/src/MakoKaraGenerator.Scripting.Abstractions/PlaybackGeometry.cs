using System.Collections.Immutable;

namespace MakoKaraGenerator.Scripting;

/// <summary>显式播放头纵向锚点；注音框不改变基础路径。</summary>
public enum CursorAnchorMode
{
    /// <summary>文字基线。</summary>
    Baseline,
    /// <summary>基础文字 Advance 高度中心。</summary>
    AdvanceCenter,
}

/// <summary>固定整形产生的进度路径；横坐标使用与渐进裁切相同的单精度计算。</summary>
public sealed record TextProgressPath
{
    /// <summary>建立真实进度路径，不按墨迹框猜横坐标。</summary>
    public TextProgressPath(float startX, float endX, double baselineY, double centerY)
    {
        if (!float.IsFinite(startX) || !float.IsFinite(endX) || !float.IsFinite(endX - startX))
            throw new ArgumentOutOfRangeException(nameof(startX));
        Point2.Finite(baselineY); Point2.Finite(centerY);
        StartX = startX; EndX = endX; BaselineY = baselineY; CenterY = centerY;
    }
    /// <summary>进度零的逻辑横坐标。</summary>
    public float StartX { get; }
    /// <summary>进度一的逻辑横坐标。</summary>
    public float EndX { get; }
    /// <summary>基线逻辑纵坐标。</summary>
    public double BaselineY { get; }
    /// <summary>基础 Advance 高度中心。</summary>
    public double CenterY { get; }
    /// <summary>与裁切相同的单精度插值；进度超界不静默夹值。</summary>
    public static float XAt(float startX, float endX, double progress)
    {
        if (!float.IsFinite(startX) || !float.IsFinite(endX) || !double.IsFinite(progress) || progress is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(progress));
        float result = progress == 1 ? endX : startX + (endX - startX) * (float)progress;
        if (!float.IsFinite(result)) throw new EffectOutputException("InvalidProgressPath", "进度路径运算超出有限坐标。");
        return result;
    }
    /// <summary>按明确纵向模式求进度点。</summary>
    public Point2 At(double progress, CursorAnchorMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return new(XAt(StartX, EndX, progress), mode == CursorAnchorMode.Baseline ? BaselineY : CenterY);
    }
}

/// <summary>一个真实源行装载，不能用 NodeId 冒充源行。</summary>
/// <param name="LineId">真实源行。</param><param name="BindingId">真实装载。</param>
public sealed record LineTarget(LineId LineId, BindingId BindingId);

/// <summary>完整行的固定字形与进度路径，包括没有逐字元素的行。</summary>
public sealed record LineGeometrySample
{
    /// <summary>真实源分支。</summary>
    public required BranchId BranchId { get; init; }
    /// <summary>真实源行装载。</summary>
    public required LineTarget Target { get; init; }
    /// <summary>完整基础字形几何，不将注音当成整行进度。</summary>
    public required GeometrySample BaseGeometry { get; init; }
    /// <summary>整行真实 Advance 进度路径。</summary>
    public required TextProgressPath ProgressPath { get; init; }
}

/// <summary>已完成文字变换后的歌曲播放头；不是用户拖拽播放头或 UI 设备位置。</summary>
/// <param name="TrackId">真实分支程序通道。</param><param name="Identity">源与装载。</param>
/// <param name="TimeUs">帧时刻。</param><param name="Progress">真实源进度。</param><param name="Position">逻辑画布点。</param>
/// <param name="Direction">画布单位方向，零长路径为零。</param><param name="AnchorMode">显式锚点选择。</param>
public sealed record PlaybackCursor(CursorTrackId TrackId, CursorIdentity Identity, long TimeUs, double Progress,
    Point2 Position, Vector2 Direction, CursorAnchorMode AnchorMode)
{
    /// <summary>真实源元素；整行进度没有假元素。</summary>
    public TokenId? TokenId => Identity.TokenId;
    /// <summary>真实源行。</summary>
    public LineId LineId => Identity.LineId;
    /// <summary>实际装载。</summary>
    public BindingId BindingId => Identity.BindingId;
    /// <summary>真实演唱身份；整行进度为空。</summary>
    public OccurrenceId? OccurrenceId => Identity.OccurrenceId;
    /// <summary>源进度依据。</summary>
    public ProgressSource ProgressSource => Identity.ProgressSource;
}

/// <summary>已验证的当前进度与最终矩阵；由消费者显式选纵向锚点。</summary>
public sealed record CursorGeometry
{
    /// <summary>真实通道。</summary>
    public required CursorTrackId TrackId { get; init; }
    /// <summary>真实源／装载身份。</summary>
    public required CursorIdentity Identity { get; init; }
    /// <summary>当前帧时刻。</summary>
    public required long TimeUs { get; init; }
    /// <summary>真实 Current 进度。</summary>
    public required double Progress { get; init; }
    /// <summary>固定整形路径。</summary>
    public required TextProgressPath Path { get; init; }
    /// <summary>与最终文字相同的逻辑到画布矩阵。</summary>
    public required Affine2 WorldMatrix { get; init; }
    /// <summary>取得真实世界播放头，不再次乘父组。</summary>
    public PlaybackCursor At(CursorAnchorMode mode)
    {
        var point = WorldMatrix.Map(Path.At(Progress, mode));
        double sign = Math.Sign((double)Path.EndX - Path.StartX);
        double x = WorldMatrix.A * sign, y = WorldMatrix.B * sign;
        double magnitude = Math.Sqrt(x * x + y * y);
        if (!double.IsFinite(magnitude)) throw new EffectOutputException("InvalidCursorGeometry", "播放头方向运算溢出。");
        return new(TrackId, Identity, TimeUs, Progress, point,
            magnitude == 0 ? Vector2.Zero : new(x / magnitude, y / magnitude), mode);
    }
}

/// <summary>完整合法播放头集合；并发保留多个，不猜唯一播放头。</summary>
public sealed class PlaybackFrame
{
    /// <summary>建立已验证的当前集合。</summary>
    public PlaybackFrame(ImmutableArray<CursorGeometry> cursors)
    {
        if (cursors.IsDefault) throw new ArgumentException("播放头集合不能是 default。", nameof(cursors));
        foreach (var cursor in cursors)
        {
            if (cursor is null || cursor.Path is null || cursor.Identity is null || !cursor.TrackId.IsValid
                || !cursor.Identity.TimeRange.Contains(cursor.TimeUs) || !double.IsFinite(cursor.Progress) || cursor.Progress is < 0 or > 1)
                throw new EffectOutputException("InvalidCursorGeometry", "播放头必须反映真实半开当前区间。");
            _ = cursor.At(CursorAnchorMode.Baseline); _ = cursor.At(CursorAnchorMode.AdvanceCenter);
        }
        Cursors = cursors;
    }
    /// <summary>原有稳定顺序的几何集合。</summary>
    public ImmutableArray<CursorGeometry> Cursors { get; }
    /// <summary>后端是否提供真实进度几何；不可用与当前没有演唱明确区分。</summary>
    public bool IsAvailable { get; private init; } = true;
    /// <summary>后端未提供几何时明确不可用，不伪造空播放头。</summary>
    public static PlaybackFrame Unavailable => new([]) { IsAvailable = false };
    /// <summary>显式选择锚点后的完整集合。</summary>
    public ImmutableArray<PlaybackCursor> GetCursors(CursorAnchorMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (!IsAvailable) throw new EffectOutputException("PlaybackGeometryUnavailable", "后端未提供真实播放头几何。");
        return [.. Cursors.Select(cursor => cursor.At(mode))];
    }
}
