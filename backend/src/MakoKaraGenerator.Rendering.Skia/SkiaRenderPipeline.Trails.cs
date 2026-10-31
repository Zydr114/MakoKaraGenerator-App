using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private static void DrawTrail(SKCanvas canvas, Fx.Trail trail, SKPaint paint, CancellationToken cancellationToken)
    {
        List<Fx.TrailPoint> points = [];
        foreach (var point in trail.Points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (points.Count > 0 && points[^1].Position == point.Position) points[^1] = point;
            else points.Add(point);
        }
        if (points.Count < 2) return;
        List<SKPoint> vertices = []; List<SKColor> colors = [];
        var previous = TrailNormal(points[0].Position, points[1].Position);
        Add(points[0], previous);
        for (int index = 1; index < points.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var next = index + 1 < points.Count ? TrailNormal(points[index].Position, points[index + 1].Position) : previous;
            double x = previous.X + next.X, y = previous.Y + next.Y;
            double length = Math.Sqrt(x * x + y * y);
            if (length <= 1e-12)
            {
                Add(points[index], previous); Flush(); Add(points[index], next);
            }
            else Add(points[index], new(x / length, y / length));
            previous = next;
        }
        Flush();
        void Add(Fx.TrailPoint point, Fx.Vector2 normal)
        {
            double half = point.Width / 2;
            vertices.Add(new((float)(point.Position.X + normal.X * half), (float)(point.Position.Y + normal.Y * half)));
            vertices.Add(new((float)(point.Position.X - normal.X * half), (float)(point.Position.Y - normal.Y * half)));
            var color = OverlayColor(point.Color); colors.Add(color); colors.Add(color);
        }
        void Flush()
        {
            if (vertices.Count >= 4)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var mesh = SKVertices.CreateCopy(SKVertexMode.TriangleStrip, [.. vertices], [.. colors])
                    ?? throw new PreparedFrameException("NativeOverlayAllocationFailed", "无法创建固定拖尾顶点。");
                canvas.DrawVertices(mesh, SKBlendMode.Modulate, paint);
            }
            vertices.Clear(); colors.Clear();
        }
    }
    private static Fx.Vector2 TrailNormal(Fx.Point2 start, Fx.Point2 end)
    {
        double x = end.X - start.X, y = end.Y - start.Y;
        double scale = Math.Max(Math.Abs(x), Math.Abs(y));
        x /= scale; y /= scale;
        double length = Math.Sqrt(x * x + y * y);
        return new(-y / length, x / length);
    }
}
