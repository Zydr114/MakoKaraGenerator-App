using System;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record StrokeData(MediaQueryId Behind, double WidthPx, double Threshold, double Busy, Vector2 Canvas) : PreparedEffect;

/// <summary>按字后方的背景亮度与纹理反差切换描边颜色和宽度；分析未就绪时保持工程原描边。</summary>
public sealed class AdaptiveStroke : IPureKaraokeEffect
{
    private static readonly ColorRgba Dark = new(0.04, 0.04, 0.07, 1);
    private static readonly ColorRgba Light = new(0.98, 0.98, 1, 1);

    public PreparedEffect Declare(DeclareContext source) => new StrokeData(source.Media.GetId("behind"),
        source.Parameters.GetNumber("widthPx"), source.Parameters.GetNumber("threshold"),
        source.Parameters.GetNumber("busyBoost"), source.CanvasSize);

    public void Evaluate(in TextFrame frame, PreparedEffect data, TextOutput output)
    {
        var settings = (StrokeData)data;
        foreach (var token in frame.Geometry)
        {
            // 与邻字共享字簇（如连字）时不能单独改外观，保持工程描边。
            if (!token.BaseGeometry.CanTransformIndependently) continue;
            if (Area(token.BaseGeometry, settings.Canvas) is not { } area) continue;
            var sample = frame.Media.Sample(settings.Behind, area, frame.TimeUs);
            // Pending／Unavailable／NoMedia 不写入：沿用工程样式里的描边。
            if (sample.CanvasValues is not { } values) continue;
            double luminance = values.MeanPremulY;
            double busy = Math.Min(1, Math.Sqrt(values.PremulYVariance) * 4);
            var color = luminance >= settings.Threshold ? Dark : Light;
            output.SetStrokes(token.WithRuby, TextState.Both, new StrokeValue(settings.WidthPx * (1 + settings.Busy * busy), MaterialValue.Solid(color)));
        }
    }

    /// <summary>字形推进框映射到画布后的轴对齐包围盒，裁剪到画布内；完全在画布外时不采样。</summary>
    private static Rect2? Area(GeometrySample geometry, Vector2 canvas)
    {
        var bounds = geometry.AdvanceBounds;
        Point2[] corners =
        [
            geometry.MapPoint(new(bounds.Left, bounds.Top)), geometry.MapPoint(new(bounds.Right, bounds.Top)),
            geometry.MapPoint(new(bounds.Right, bounds.Bottom)), geometry.MapPoint(new(bounds.Left, bounds.Bottom)),
        ];
        double left = Math.Max(0, Math.Min(Math.Min(corners[0].X, corners[1].X), Math.Min(corners[2].X, corners[3].X)));
        double top = Math.Max(0, Math.Min(Math.Min(corners[0].Y, corners[1].Y), Math.Min(corners[2].Y, corners[3].Y)));
        double right = Math.Min(canvas.X, Math.Max(Math.Max(corners[0].X, corners[1].X), Math.Max(corners[2].X, corners[3].X)));
        double bottom = Math.Min(canvas.Y, Math.Max(Math.Max(corners[0].Y, corners[1].Y), Math.Max(corners[2].Y, corners[3].Y)));
        return right - left >= 1 && bottom - top >= 1 ? new Rect2(left, top, right, bottom) : null;
    }
}
