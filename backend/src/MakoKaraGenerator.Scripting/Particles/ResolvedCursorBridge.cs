namespace MakoKaraGenerator.Scripting.Particles;

// 固定 SDK 值；独立查询的作者实例及几何后端不进入主模拟。
internal sealed record ResolvedCursorBridge(ParticleSource.Bridge Source, Point2 Previous, Point2 Next, CurveValue Curve)
{
    public Point2 At(long timeUs)
    {
        double progress = Curve.Evaluate((timeUs - Source.FlightRange.StartUs) / (double)Source.FlightRange.DurationUs);
        return new(Previous.X * (1 - progress) + Next.X * progress, Previous.Y * (1 - progress) + Next.Y * progress);
    }
}
