using MakoKaraGenerator.Core.Effects;
using MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

/// <summary>同一行／标记绘制期间至多保留一张无输入作者图；真实预留在预算压力下可被驱逐。</summary>
internal sealed class MaterialAuthorGraphCache(ImageSurfaceBudgetFrame resources) : IDisposable
{
    private const long MaximumRetainedBytes = 16 * 1024 * 1024;
    private CompiledImageGraph? _graph;
    private ImageGraphResult? _result;
    private IDisposable? _reclaim;
    private bool _disposed;

    internal ImageGraphResult? Take(CompiledImageGraph graph, ImageGraphFrameContext frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = _result;
        bool matches = ReferenceEquals(_graph, graph) && result is not null && MatchesFrame(graph, result.Context, frame);
        _reclaim?.Dispose(); _reclaim = null;
        _graph = null; _result = null;
        if (matches) return result;
        result?.Dispose(); return null;
    }

    // true 才转移所有权；拒绝和异常由调用方释放原结果。
    internal bool TryStore(CompiledImageGraph graph, ImageGraphResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Clear();
        long bytes = checked((long)result.Output.Image.Width * result.Output.Image.Height * 4);
        if (!graph.Plan.Definition.Inputs.IsEmpty || graph.Plan.Definition.CompositeCoverage is not null
            || result.Coverage is not null || !result.Diagnostics.IsEmpty || bytes > MaximumRetainedBytes)
            return false;
        _graph = graph; _result = result;
        try { _reclaim = resources.RegisterReclaimable(result.Output.Image.Handle, Clear); }
        catch { _graph = null; _result = null; throw; }
        return true;
    }

    // 图实例固定包修订、编译计划、设备和色彩编码；无声明输入排除目标／背景／资源依赖。
    // 浮点按位比较，避免把 shader 可观察的 +0/-0 当成同一输入。不使用有碰撞的摘要键。
    internal static bool MatchesFrame(CompiledImageGraph graph, ImageGraphFrameContext a, ImageGraphFrameContext b)
        => a.TimeUs == b.TimeUs && Same(a.CanvasWidth, b.CanvasWidth) && Same(a.CanvasHeight, b.CanvasHeight)
            && Same(a.TargetBounds.Left, b.TargetBounds.Left) && Same(a.TargetBounds.Top, b.TargetBounds.Top)
            && Same(a.TargetBounds.Right, b.TargetBounds.Right) && Same(a.TargetBounds.Bottom, b.TargetBounds.Bottom)
            && Same(a.DensityX, b.DensityX) && Same(a.DensityY, b.DensityY) && a.Transparent == b.Transparent
            && SameParameters(graph, a.Parameters, b.Parameters) && SameUniforms(a.Uniforms, b.Uniforms);

    private static bool SameParameters(CompiledImageGraph graph, ParameterValues a, ParameterValues b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (!a.Ids.SequenceEqual(b.Ids)) return false;
        foreach (string id in a.Ids)
        {
            if (!graph.Plan.Parameters.TryGetValue(id, out var parameter)) return false;
            bool same = parameter switch
            {
                EffectParameterConstraint.Number => Same(a.GetNumber(id), b.GetNumber(id)),
                EffectParameterConstraint.Whole => a.GetInteger(id) == b.GetInteger(id),
                EffectParameterConstraint.Toggle => a.GetBoolean(id) == b.GetBoolean(id),
                EffectParameterConstraint.Color => SameColor(a.GetColor(id), b.GetColor(id)),
                EffectParameterConstraint.Choice => StringComparer.Ordinal.Equals(a.GetEnum(id), b.GetEnum(id)),
                EffectParameterConstraint.Duration => a.GetDuration(id) == b.GetDuration(id),
                EffectParameterConstraint.Text => StringComparer.Ordinal.Equals(a.GetText(id), b.GetText(id)),
                // 不把未知的曲线／资源对象视作值相同；同一不可变参数集仍可走上面的引用快路。
                EffectParameterConstraint.Curve => ReferenceEquals(a.GetCurve(id), b.GetCurve(id)),
                EffectParameterConstraint.Asset => ReferenceEquals(a.GetAsset(id), b.GetAsset(id)),
                _ => false,
            };
            if (!same) return false;
        }
        return true;
    }

    private static bool SameColor(ColorRgba a, ColorRgba b)
        => Same(a.Red, b.Red) && Same(a.Green, b.Green) && Same(a.Blue, b.Blue) && Same(a.Alpha, b.Alpha);
    private static bool Same(double a, double b) => BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);
    private static bool SameUniforms(GraphUniformValues a, GraphUniformValues b)
    {
        if (ReferenceEquals(a, b)) return true;
        return a.Values.Count == b.Values.Count && a.Values.All(pair => b.Values.TryGetValue(pair.Key, out var value)
            && pair.Value.Kind == value.Kind && pair.Value.ArrayLength == value.ArrayLength
            && pair.Value.Numbers.Length == value.Numbers.Length && pair.Value.Numbers.Zip(value.Numbers).All(item => Same(item.First, item.Second))
            && pair.Value.Integers.SequenceEqual(value.Integers));
    }

    internal void Clear()
    {
        _reclaim?.Dispose(); _reclaim = null;
        var result = _result; _graph = null; _result = null;
        result?.Dispose();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Clear(); }
}
