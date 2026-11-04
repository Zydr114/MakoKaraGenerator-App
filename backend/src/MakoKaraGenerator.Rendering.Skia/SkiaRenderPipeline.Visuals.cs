using SkiaSharp;
using Fx = MakoKaraGenerator.Scripting;

namespace MakoKaraGenerator.Rendering.Skia;

public sealed partial class SkiaRenderPipeline
{
    private void ValidateNativeOverlays(PreparedRenderFrame prepared)
    {
        foreach (var batch in prepared.OverlayBatches)
            OverlayOperation(prepared, batch, batch is Fx.SpriteBatch ? "Sprite" : "Overlay", () =>
            {
                switch (batch)
                {
                case Fx.SpriteBatch sprites:
                    foreach (var sprite in sprites.Sprites) { ValidateNativeSprite(sprite); _ = SpriteImage(sprite); }
                    break;
                case Fx.PathBatch paths:
                    foreach (var path in paths.Paths) ValidateNativeVector(path);
                    break;
                case Fx.AtlasSpriteBatch atlas:
                    foreach (var sprite in atlas.Sprites) ValidateNativeAtlasSprite(sprite);
                    break;
                case Fx.DotBatch dots:
                    foreach (var dot in dots.Dots)
                    {
                        NativePoint(dot.Position, dot.Radius * 2, positive: true);
                        if ((float)dot.Radius <= 0) throw new PreparedFrameException("UnsupportedNativeOverlay", "圆点半径低于后端精度。");
                    }
                    break;
                case Fx.SegmentBatch segments:
                    foreach (var segment in segments.Segments)
                    { NativePoint(segment.Start, segment.Width, positive: true); NativePoint(segment.End, segment.Width, positive: true); }
                    break;
                case Fx.TrailBatch trails:
                    foreach (var point in trails.Trails.SelectMany(static trail => trail.Points)) NativePoint(point.Position, point.Width, positive: false);
                    break;
                default:
                    throw new PreparedFrameException("UnsupportedOverlayBatch", "后端不认识此封闭图元批次，不能忽略输出。");
                }
                return true;
            });
    }
    private static void NativePoint(Fx.Point2 position, double width, bool positive)
    {
        if (!float.IsFinite((float)position.X) || !float.IsFinite((float)position.Y)
            || !float.IsFinite((float)width) || (width > 0 || positive) && (float)width <= 0
            || !float.IsFinite((float)(position.X - width / 2)) || !float.IsFinite((float)(position.X + width / 2))
            || !float.IsFinite((float)(position.Y - width / 2)) || !float.IsFinite((float)(position.Y + width / 2)))
            throw new PreparedFrameException("UnsupportedNativeOverlay", "图元位置、宽度或外边界超出后端精度，不能静默丢弃或夹值。");
    }
    private static SKColor OverlayColor(Fx.ColorRgba color) => new(OverlayByte(color.Red), OverlayByte(color.Green), OverlayByte(color.Blue), OverlayByte(color.Alpha));
    private static byte OverlayByte(double value) => checked((byte)Math.Round(value * 255));
    private void DrawOverlays(SKCanvas canvas, RenderRequest request, Fx.OverlayLayer layer,
        ImageSurfaceBudgetFrame resources, SvgRenderWorkBudget svgWork)
    {
        if (request.PreparedFrame is not { } frame) return;
        using SKPaint paint = new() { IsAntialias = request.Quality != RenderQuality.Draft };
        foreach (var batch in frame.OverlayBatches.Where(batch => batch.Layer == layer))
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            switch (batch)
            {
                case Fx.DotBatch dots:
                    paint.Style = SKPaintStyle.Fill;
                    foreach (var dot in dots.Dots)
                    {
                        request.CancellationToken.ThrowIfCancellationRequested(); paint.Color = OverlayColor(dot.Color);
                        canvas.DrawCircle((float)dot.Position.X, (float)dot.Position.Y, (float)dot.Radius, paint);
                    }
                    break;
                case Fx.SegmentBatch segments:
                    paint.Style = SKPaintStyle.Stroke; paint.StrokeCap = SKStrokeCap.Butt;
                    foreach (var segment in segments.Segments)
                    {
                        request.CancellationToken.ThrowIfCancellationRequested(); paint.Color = OverlayColor(segment.Color); paint.StrokeWidth = (float)segment.Width;
                        canvas.DrawLine((float)segment.Start.X, (float)segment.Start.Y, (float)segment.End.X, (float)segment.End.Y, paint);
                    }
                    break;
                case Fx.SpriteBatch sprites:
                    OverlayOperation(frame, sprites, "Sprite", () => { DrawSprites(canvas, sprites, request); return true; });
                    break;
                case Fx.PathBatch paths:
                    OverlayOperation(frame, paths, "SvgVector", () => { DrawPaths(canvas, paths, request, resources, svgWork); return true; });
                    break;
                case Fx.AtlasSpriteBatch atlas:
                    OverlayOperation(frame, atlas, "SvgAtlas", () => { DrawAtlasSprites(canvas, atlas, request, svgWork); return true; });
                    break;
                case Fx.TrailBatch trails:
                    paint.Style = SKPaintStyle.Fill; paint.Color = SKColors.White;
                    foreach (var trail in trails.Trails) DrawTrail(canvas, trail, paint, request.CancellationToken);
                    break;
                default:
                    OverlayOperation<bool>(frame, batch, "Overlay", () => throw new PreparedFrameException("UnsupportedOverlayBatch", "后端不认识此封闭图元批次，不能忽略输出。"));
                    break;
            }
        }
    }
}
