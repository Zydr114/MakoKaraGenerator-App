using System;
using System.Collections.Immutable;
using MakoKaraGenerator.Scripting;
using MakoKaraGenerator.Scripting.V2;

public sealed record StarData(VectorHandle Star, double SizePx, double SpinHz, ColorRgba Tint, int TailCount, double TailStep) : PreparedEffect;

/// <summary>SVG 矢量星沿每个真实播放头扫过整句，后随渐隐小星。位置取 Warp／父组之后的最终几何。</summary>
public sealed class StarSweep : IPureKaraokeEffect<StarData>, IPureVisualEffect<StarData>
{
    public StarData Declare(DeclareContext source)
    {
        var p = source.Parameters;
        return new StarData(source.Resources.GetVector("star"), p.GetNumber("sizePx"), p.GetNumber("spinHz"), p.GetColor("color"),
            (int)p.GetInteger("tailCount"), p.GetNumber("tailStep"))
        {
            VisualOutputs = [new(new("stars"), VisualPrimitiveKind.Paths, OverlayLayer.AboveText, VisualResourceKind.SvgVector)],
        };
    }

    public void Evaluate(in TextFrame frame, StarData data, TextOutput output) { }

    public void EvaluateVisuals(in VisualFrame frame, StarData d, VisualOutput output)
    {
        var stars = ImmutableArray.CreateBuilder<VectorVisual>();
        var playback = frame.FinalGeometry.Playback;
        if (playback.IsAvailable)
        {
            double spin = 2 * Math.PI * d.SpinHz * frame.TimeUs / 1_000_000d;
            foreach (var cursor in playback.Cursors)
            {
                // 尾星沿同一进度路径回溯，经同一映射落到最终几何上，不按直线外推。
                for (int index = d.TailCount; index >= 0; index--)
                {
                    double progress = cursor.Progress - index * d.TailStep;
                    if (progress < 0) continue;
                    var point = (cursor with { Progress = progress }).At(CursorAnchorMode.AdvanceCenter).Position;
                    double fade = 1 - index / (double)(d.TailCount + 1);
                    double size = d.SizePx * (0.45 + 0.55 * fade);
                    var tint = new ColorRgba(d.Tint.Red, d.Tint.Green, d.Tint.Blue, d.Tint.Alpha * fade);
                    stars.Add(new(d.Star, Spin(point, size, spin + index * 0.6), tint));
                }
            }
        }
        output.DrawPaths(new("stars"), stars.ToImmutable(), OverlayLayer.AboveText);
    }

    private static Affine2 Spin(Point2 center, double size, double angle)
    {
        double a = Math.Cos(angle) * size, b = Math.Sin(angle) * size, c = -b, e = a;
        return new Affine2(a, b, c, e, center.X - (a + c) / 2, center.Y - (b + e) / 2);
    }
}
